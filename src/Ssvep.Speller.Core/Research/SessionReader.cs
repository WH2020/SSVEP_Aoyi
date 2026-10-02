using System.Security.Cryptography;
using System.Text.Json;
using Ssvep.Speller.Core.Signal;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Speller.Core.Research;

public sealed record SessionEvent(long Sequence,string Kind,double? HostSeconds,JsonElement Data);

public sealed class SessionReader
{
    public string Folder { get; }
    public JsonElement Manifest { get; }
    public RunContext Context { get; }
    public bool IsLegacy { get; }
    public List<string> Warnings { get; } = [];
    public SessionReader(string folder)
    {
        Folder=Path.GetFullPath(folder);
        using(var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder,"manifest.json")))) Manifest=doc.RootElement.Clone();
        IsLegacy=!Manifest.TryGetProperty("schemaVersion",out var version);
        if(!IsLegacy&&version.GetInt32()!=2) throw new InvalidDataException("不支持该会话 schemaVersion。");
        var content=StudioContentService.Load(Path.Combine(Folder,"content.ssvep.json"));
        var profile=IsLegacy?ResearchProfile.Legacy():ResearchProfile.Parse(File.ReadAllText(Path.Combine(Folder,"research-profile.json")));
        int rate=Manifest.GetProperty("samplingRateHz").GetInt32();
        var channels=Manifest.GetProperty("channels").EnumerateArray().Select(c=>c.GetInt32()).ToArray();
        var bindings=Manifest.TryGetProperty("electrodeBindings",out var mapped)
            ?JsonSerializer.Deserialize<StudioElectrodeBinding[]>(mapped.GetRawText(),CanonicalJson.Options)??[]:[];
        if(IsLegacy)
        {
            if(Manifest.GetProperty("profile").GetString()!=StudioRuntimeProfile.Version) throw new InvalidDataException("旧会话算法版本不明，无法重建基线。");
            var firstSample=Events("sample").FirstOrDefault();
            int rawChannels=firstSample?.Data.GetProperty("channels").GetArrayLength()??Math.Max(8,channels.Max()+1);
            Context=new(profile,content,rate,rawChannels,channels,bindings);
            Warnings.Add("旧记录没有 SDK 批次时钟与校准标签；只计算算法得分，不推断正确率。");
        }
        else
        {
            var samples=new BinarySampleReader(Path.Combine(Folder,"samples.bin"));
            var display=JsonSerializer.Deserialize<DisplayGeometry>(Manifest.GetProperty("display").GetRawText(),CanonicalJson.Options);
            Context=new(profile,content,rate,samples.Header.ChannelCount,channels,bindings,display,samples.Header.SampleUnit,samples.Header.ScaleToVolts,samples.Header.UnitSource);
            if(Context.RunConfigHash!=Manifest.GetProperty("runConfigHash").GetString()||Context.RunConfigHash!=samples.Header.RunConfigHash)
                throw new InvalidDataException("会话配置指纹不一致。");
        }
        if(!Manifest.GetProperty("completed").GetBoolean()) Warnings.Add("会话未完整结束，仅分析可恢复记录。");
    }

    public void VerifyFiles()
    {
        if(!Manifest.TryGetProperty("files",out var files)) return;
        foreach(var property in files.EnumerateObject())
        {
            if(property.Name!=Path.GetFileName(property.Name)) throw new InvalidDataException("清单文件名无效。");
            string path=Path.Combine(Folder,property.Name);
            if(!File.Exists(path)) { Warnings.Add("缺少文件："+property.Name); continue; }
            using var stream=File.OpenRead(path);
            string hash=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if(hash!=property.Value.GetProperty("sha256").GetString()) Warnings.Add("文件指纹不匹配："+property.Name);
        }
    }

    public IEnumerable<SessionEvent> Events(params string[] kinds)
    {
        long index=0;
        foreach(string line in File.ReadLines(Path.Combine(Folder,"events.ndjson")))
        {
            if(string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? doc=null;
            try { doc=JsonDocument.Parse(line); }
            catch(JsonException) { Warnings.Add($"事件行损坏：{index+1}"); }
            if(doc is null) { index++; continue; }
            using(doc)
            {
                var root=doc.RootElement; string kind=root.GetProperty("kind").GetString()??"unknown";
                index++;
                if(kinds.Length>0&&!kinds.Contains(kind)) continue;
                yield return new(root.TryGetProperty("sequence",out var seq)?seq.GetInt64():index,kind,
                    root.TryGetProperty("hostSeconds",out var time)?time.GetDouble():null,root.GetProperty("data").Clone());
            }
        }
    }

    public IEnumerable<RecordedBatch> ReadSamples()
    {
        if(!IsLegacy)
        {
            var reader=new BinarySampleReader(Path.Combine(Folder,"samples.bin"));
            foreach(var batch in reader.Read()) yield return batch;
            Warnings.AddRange(reader.Warnings);
            yield break;
        }
        long run=0,batchId=0; bool enabled=false;
        foreach(var item in Events("sample","state","frame"))
        {
            if(item.Kind=="state")
            {
                string? state=item.Data.GetProperty("state").GetString();
                if(state=="started") { run=item.Data.GetProperty("run").GetInt64(); enabled=false; }
                else enabled=false;
            }
            else if(item.Kind=="frame") enabled=true;
            else
            {
                var sample=JsonSerializer.Deserialize<EegSample>(item.Data.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var info=new SampleBlockInfo(++batchId,batchId,"legacy",run,0,sample.SampleIndex,1,0,0,"legacy-unmapped",null,null,"derived-from-sample-index",enabled);
                yield return new(info,new(batchId,"legacy",Context.SamplingRateHz,[sample],0,0,"legacy-unmapped"),0,0);
            }
        }
    }
}
