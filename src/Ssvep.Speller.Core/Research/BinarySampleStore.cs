using System.Text;
using System.Text.Json;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Research;

public sealed record BinarySampleHeader(int Version, int SamplingRateHz, int ChannelCount, string RunConfigHash,
    string SampleUnit, double? ScaleToVolts, string UnitSource, IReadOnlyList<string> ChannelNames,
    string NumericType = "float64", string ByteOrder = "little", string Layout = "sample-major");
public sealed record SampleBlockInfo(long Sequence, long BatchId, string ClockEpoch, long RunGeneration, long Continuity,
    long FirstSampleIndex, int Count, double SourceReceivedSeconds, double HostReceivedSeconds, string SourceClock,
    string? DomainModelId, double? MappedHostSeconds, string DeviceTimeSource, bool RecognitionEnabled = true);
public sealed record RecordedBatch(SampleBlockInfo Info, SampleBatch Batch, long FileOffset, int FileLength);

public static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc=uint.MaxValue;
        foreach(byte b in bytes) { crc^=b; for(int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)==0 ? 0u : 0x82F63B78u); }
        return ~crc;
    }
}

public sealed class BinarySampleWriter : IDisposable
{
    private readonly BinaryWriter writer;
    private readonly BinaryWriter index;
    private readonly BinarySampleHeader header;
    private long sequence;
    public const string Format = "ssvep-binary-v1";
    internal const uint BlockMagic = 0x314B4C42;

    public BinarySampleWriter(string folder, BinarySampleHeader header)
    {
        this.header=header;
        writer=new(new FileStream(Path.Combine(folder,"samples.bin"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536));
        try
        {
            writer.Write(Encoding.ASCII.GetBytes("SSVEPB01"));
            byte[] metadata=JsonSerializer.SerializeToUtf8Bytes(header,CanonicalJson.Options);
            writer.Write(metadata.Length); writer.Write(metadata); writer.Flush();
            index=new(new FileStream(Path.Combine(folder,"samples.idx"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,16384));
            index.Write(Encoding.ASCII.GetBytes("SSVEPI01")); index.Flush();
        }
        catch { writer.Dispose(); throw; }
    }

    public void Write(SampleBatch batch, long run, long continuity, bool recognitionEnabled = true)
    {
        if(batch.SamplingRateHz != header.SamplingRateHz || batch.Samples.Any(s => s.Channels.Length!=header.ChannelCount)) throw new InvalidDataException("样本格式与二进制头不一致。");
        int start=0;
        while(start<batch.Samples.Count)
        {
            int count=1;
            while(start+count<batch.Samples.Count && count<header.SamplingRateHz
                && batch.Samples[start+count].SampleIndex==batch.Samples[start+count-1].SampleIndex+1
                && batch.Samples[start+count].IsLost==batch.Samples[start].IsLost) count++;
            var info=new SampleBlockInfo(sequence++,batch.BatchId,batch.ClockEpoch,run,continuity,batch.Samples[start].SampleIndex,count,
                batch.SourceReceivedSeconds,batch.HostReceivedSeconds,batch.SourceClock,batch.DomainModelId,batch.MappedHostSeconds,batch.DeviceTimeSource,recognitionEnabled);
            using var body=new MemoryStream();
            using(var block=new BinaryWriter(body,Encoding.UTF8,true))
            {
                byte[] metadata=JsonSerializer.SerializeToUtf8Bytes(info,CanonicalJson.Options);
                block.Write(metadata.Length); block.Write(metadata);
                for(int row=start;row<start+count;row++)
                {
                    var sample=batch.Samples[row];
                    block.Write(sample.Timestamp.UtcTicks); block.Write(sample.DeviceElapsedMilliseconds??double.NaN); block.Write(sample.IsLost);
                    ulong valid=0;
                    for(int c=0;c<header.ChannelCount;c++) if(!sample.IsLost && double.IsFinite(sample.Channels[c])) valid|=1UL<<c;
                    block.Write(valid);
                    foreach(double value in sample.Channels) block.Write(value);
                }
            }
            byte[] bytes=body.ToArray(); long offset=writer.BaseStream.Position;
            writer.Write(BlockMagic); writer.Write(bytes.Length); writer.Write(Crc32C.Compute(bytes)); writer.Write(bytes);
            WriteIndex(index,info.Sequence,offset,bytes.Length+12,info.FirstSampleIndex,count);
            start+=count;
        }
    }

    internal static void WriteIndex(BinaryWriter writer,long sequence,long offset,int length,long first,int count)
    { writer.Write(sequence); writer.Write(offset); writer.Write(length); writer.Write(first); writer.Write(count); }
    public void Dispose() { try { writer.Dispose(); } finally { index.Dispose(); } }
}

public sealed class BinarySampleReader
{
    private readonly string path;
    public BinarySampleHeader Header { get; }
    public List<string> Warnings { get; } = [];
    private readonly long dataOffset;

    public BinarySampleReader(string path)
    {
        this.path=path;
        using var reader=new BinaryReader(File.OpenRead(path));
        if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="SSVEPB01") throw new InvalidDataException("未知样本文件头。");
        int length=reader.ReadInt32();
        if(length<=0 || length>1_048_576) throw new InvalidDataException("样本文件头长度无效。");
        var bytes=reader.ReadBytes(length);
        if(bytes.Length!=length) throw new InvalidDataException("样本文件头被截断。");
        Header=JsonSerializer.Deserialize<BinarySampleHeader>(bytes,CanonicalJson.Options)??throw new InvalidDataException("空样本头。");
        if(Header.Version!=1 || Header.ChannelCount is <1 or >64 || Header.SamplingRateHz is not (250 or 500)
            || Header.NumericType!="float64" || Header.ByteOrder!="little" || Header.Layout!="sample-major" || Header.ChannelNames.Count!=Header.ChannelCount)
            throw new InvalidDataException("不支持的样本格式。");
        dataOffset=reader.BaseStream.Position;
    }

    public IEnumerable<RecordedBatch> Read()
    {
        Warnings.Clear();
        using var reader=new BinaryReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
        reader.BaseStream.Position=dataOffset;
        while(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            long offset=reader.BaseStream.Position, remaining=reader.BaseStream.Length-offset;
            if(remaining<12) { Warnings.Add($"尾部未提交块：{offset}"); yield break; }
            uint magic=reader.ReadUInt32(); int length=reader.ReadInt32(); uint crc=reader.ReadUInt32();
            if(magic!=BinarySampleWriter.BlockMagic || length is <4 or >16_777_216) { Warnings.Add($"块头损坏，停止读取：{offset}"); yield break; }
            if(reader.BaseStream.Length-reader.BaseStream.Position<length) { Warnings.Add($"尾部未提交块：{offset}"); yield break; }
            byte[] body=reader.ReadBytes(length);
            if(Crc32C.Compute(body)!=crc) { Warnings.Add($"CRC32C 失败，隔离块：{offset}"); continue; }
            RecordedBatch? batch;
            try { batch=Decode(body,offset,length+12); }
            catch(Exception ex) when(ex is JsonException or InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            { Warnings.Add($"无效块 {offset}：{ex.Message}"); continue; }
            yield return batch;
        }
    }

    private RecordedBatch Decode(byte[] bytes,long offset,int fileLength)
    {
        using var block=new BinaryReader(new MemoryStream(bytes));
        int length=block.ReadInt32();
        if(length<=0 || length>bytes.Length-4) throw new InvalidDataException("块元数据长度无效。");
        var info=JsonSerializer.Deserialize<SampleBlockInfo>(block.ReadBytes(length),CanonicalJson.Options)??throw new InvalidDataException("空块元数据。");
        int stride=25+8*Header.ChannelCount;
        if(info.Count<=0 || info.Count>Header.SamplingRateHz || bytes.Length-4-length!=info.Count*stride) throw new InvalidDataException("块样本长度不匹配。");
        var samples=new EegSample[info.Count];
        for(int row=0;row<samples.Length;row++)
        {
            var utc=new DateTimeOffset(block.ReadInt64(),TimeSpan.Zero);
            double device=block.ReadDouble(); bool lost=block.ReadBoolean(); ulong valid=block.ReadUInt64();
            var values=new double[Header.ChannelCount];
            for(int c=0;c<values.Length;c++)
            {
                values[c]=block.ReadDouble();
                bool expected=!lost&&double.IsFinite(values[c]);
                if(((valid>>c)&1)!= (expected?1UL:0UL)) throw new InvalidDataException("通道有效位图不匹配。");
            }
            samples[row]=new(checked(info.FirstSampleIndex+row),utc,values,double.IsNaN(device)?null:device,lost);
        }
        return new(info,new(info.BatchId,info.ClockEpoch,Header.SamplingRateHz,samples,info.SourceReceivedSeconds,info.HostReceivedSeconds,
            info.SourceClock,info.DomainModelId,info.MappedHostSeconds,info.DeviceTimeSource),offset,fileLength);
    }

    public void RebuildIndex(string destination)
    {
        using var writer=new BinaryWriter(new FileStream(destination,FileMode.CreateNew,FileAccess.Write));
        writer.Write(Encoding.ASCII.GetBytes("SSVEPI01"));
        foreach(var batch in Read()) BinarySampleWriter.WriteIndex(writer,batch.Info.Sequence,batch.FileOffset,batch.FileLength,batch.Info.FirstSampleIndex,batch.Info.Count);
    }
}
