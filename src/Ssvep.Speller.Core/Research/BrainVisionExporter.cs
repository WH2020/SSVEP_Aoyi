using System.Globalization;
using System.Text;
using System.Text.Json;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Research;

public sealed record BrainVisionSegment(string HeaderFile,long FirstSampleIndex,long LastSampleIndex,int SampleCount,
    string ClockEpoch,long Run,bool RecognitionEnabled,string? ClockModelId,double? EstimatedHostStartSeconds,int EventCount);
public sealed record BrainVisionExportResult(string Directory,int SegmentCount,long SampleCount,IReadOnlyList<string> Warnings);

/// <summary>Streaming BrainVision Core Data Format 1.0 writer. No runtime Python dependency for offline export.</summary>
public static class BrainVisionExporter
{
    private static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
    public static BrainVisionExportResult Export(string sessionFolder,string outputRoot,CancellationToken cancellation=default)
    {
        var reader=new SessionReader(sessionFolder); reader.VerifyFiles(); var context=reader.Context;
        if(context.ScaleToVolts is not double scale || context.SampleUnit=="unknown") throw new InvalidOperationException("采集单位未知，无法生成带物理单位的科研数据。请保留原始记录并核验 SDK 单位。");
        var events=reader.Events("trial-marker","trial-exclusion","clock-model","clock-observation","filter-reset","state","sample-loss").ToArray();
        var markers=events.Where(e=>e.Kind=="trial-marker").Select(e=>JsonSerializer.Deserialize<TrialMarker>(e.Data.GetRawText(),CanonicalJson.Options)!).ToArray();
        var models=events.Where(e=>e.Kind=="clock-model").Select(e=>JsonSerializer.Deserialize<ClockModel>(e.Data.GetRawText(),CanonicalJson.Options)!).ToDictionary(m=>m.ModelId);
        var names=Enumerable.Range(0,context.ChannelCount).Select(c=>context.ElectrodeBindings.FirstOrDefault(b=>b.ChannelIndex==c)?.Electrode??$"CH{c+1}").ToArray();
        string folder=Path.Combine(outputRoot,"BrainVision-"+reader.Manifest.GetProperty("sessionId").GetString()+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var segments=new List<BrainVisionSegment>(); var placed=new HashSet<int>();
        BinaryWriter? binary=null; SampleBlockInfo? current=null; long first=0,last=-1,total=0; int count=0;
        string stem="";
        void Finish()
        {
            if(binary is null||current is null) return;
            binary.Dispose(); binary=null;
            var header=new StringBuilder("Brain Vision Data Exchange Header File Version 1.0\n\n[Common Infos]\nCodepage=UTF-8\n");
            header.Append($"DataFile={stem}.eeg\nMarkerFile={stem}.vmrk\nDataFormat=BINARY\nDataOrientation=MULTIPLEXED\nNumberOfChannels={context.ChannelCount}\nSamplingInterval={(1_000_000.0/context.SamplingRateHz).ToString("R",Invariant)}\n\n[Binary Infos]\nBinaryFormat=IEEE_FLOAT_32\n\n[Channel Infos]\n");
            for(int c=0;c<names.Length;c++) header.Append($"Ch{c+1}={Escape(names[c])},,0.1,µV\n");
            File.WriteAllText(Path.Combine(folder,stem+".vhdr"),header.ToString(),new UTF8Encoding(false));
            var vmrk=new StringBuilder($"Brain Vision Data Exchange Marker File, Version 1.0\n\n[Common Infos]\nCodepage=UTF-8\nDataFile={stem}.eeg\n\n[Marker Infos]\n");
            int number=0;
            for(int i=0;i<markers.Length;i++)
            {
                var marker=markers[i];
                if(placed.Contains(i)||marker.EstimatedSampleIndex is not double x||marker.TimingQuality!="Estimated"
                    ||marker.ModelId is null||!models.ContainsKey(marker.ModelId)) continue;
                long at=(long)Math.Round(x,MidpointRounding.AwayFromZero);
                if(at<first||at>last) continue;
                vmrk.Append($"Mk{++number}=Comment,{Escape(marker.TrialId+":"+marker.Phase+":"+(marker.TargetId??marker.Label))},{at-first+1},1,0\n");
                placed.Add(i);
            }
            File.WriteAllText(Path.Combine(folder,stem+".vmrk"),vmrk.ToString(),new UTF8Encoding(false));
            ClockModel? model=models.Values.Where(m=>m.SourceDomain=="device-sample-index"&&m.Quality=="Estimated"&&m.FirstX<=first&&m.LastX>=first).OrderBy(m=>m.LastX).FirstOrDefault();
            segments.Add(new(stem+".vhdr",first,last,count,current.ClockEpoch,current.RunGeneration,current.RecognitionEnabled,model?.ModelId,model?.Estimate(first),number));
            total+=count;
        }
        try
        {
            foreach(var stored in reader.ReadSamples())
            {
                cancellation.ThrowIfCancellationRequested();
                foreach(var sample in stored.Batch.Samples)
                {
                    if(sample.IsLost||sample.Channels.Any(v=>!double.IsFinite(v))) { Finish(); current=null; continue; }
                    bool newSegment=current is null||sample.SampleIndex!=last+1||current.ClockEpoch!=stored.Info.ClockEpoch
                        ||current.RunGeneration!=stored.Info.RunGeneration||current.RecognitionEnabled!=stored.Info.RecognitionEnabled;
                    if(newSegment)
                    {
                        Finish(); current=stored.Info; first=sample.SampleIndex; count=0;
                        stem=$"segment-{segments.Count+1:D4}";
                        binary=new(new FileStream(Path.Combine(folder,stem+".eeg"),FileMode.CreateNew,FileAccess.Write));
                    }
                    foreach(double value in sample.Channels)
                    {
                        double units=value*scale*1e6/.1;
                        if(!double.IsFinite(units)||Math.Abs(units)>float.MaxValue) throw new InvalidDataException("数据超出 BrainVision float32 范围。");
                        binary!.Write((float)units);
                    }
                    last=sample.SampleIndex; count++;
                }
            }
            Finish();
            if(segments.Count==0) throw new InvalidDataException("没有可导出的连续有效样本。");
            File.WriteAllText(Path.Combine(folder,"channels.tsv"),"index\tname\tsource_unit\tscale_to_volts\tposition_source\n"+
                string.Join("\n",names.Select((name,i)=>$"{i}\t{Tsv(name)}\t{Tsv(context.SampleUnit)}\t{scale.ToString("R",Invariant)}\t{(context.ElectrodeBindings.Any(b=>b.ChannelIndex==i)?"manual":"unmapped")}")),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder,"events.tsv"),"trial_id\tphase\tlabel\ttarget\thost_seconds\testimated_sample\tmodel_id\ttiming_quality\twritten_to_vmrk\n"+
                string.Join("\n",markers.Select((m,i)=>$"{Tsv(m.TrialId)}\t{Tsv(m.Phase)}\t{Tsv(m.Label)}\t{Tsv(m.TargetId)}\t{m.HostSeconds.ToString("R",Invariant)}\t{m.EstimatedSampleIndex?.ToString("R",Invariant)}\t{m.ModelId}\t{m.TimingQuality}\t{placed.Contains(i)}")),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder,"clock-models.json"),JsonSerializer.Serialize(models.Values,CanonicalJson.Options));
            File.WriteAllText(Path.Combine(folder,"export-manifest.json"),JsonSerializer.Serialize(new
            {
                schemaVersion=1,sourceSessionId=reader.Manifest.GetProperty("sessionId").GetString(),context.RunConfigHash,
                context.Profile.ProfileHash,sourceDataSource=reader.Manifest.GetProperty("dataSource").GetString(),samplingRateHz=context.SamplingRateHz,
                segments,totalSamples=total,warnings=reader.Warnings.Distinct(),unplacedEvents=markers.Length-placed.Count,
                outputUnit="uV",resolution=.1,binaryFormat="IEEE_FLOAT_32",originalType="float64",
                roundingToleranceVolts="abs(error) <= max(1e-15, abs(expected) * 2e-7)",
                timestamps="software-estimated; fixed acquisition/display delay uncalibrated",gapPolicy="separate files; no padding or interpolation",
                exporter="Ssvep.Studio native BrainVision v1",mneReadback="run tools/verify_brainvision.py; export alone is not validation"
            },CanonicalJson.Options));
            return new(folder,segments.Count,total,reader.Warnings.Distinct().ToArray());
        }
        catch
        {
            binary?.Dispose();
            File.WriteAllText(Path.Combine(folder,"EXPORT-INCOMPLETE.txt"),"导出未完成。不要将此目录作为完整研究数据。");
            throw;
        }
    }
    private static string Escape(string value)=>value.Replace("\r"," ").Replace("\n"," ").Replace(",","\\1");
    private static string Tsv(string? value)=>(value??"").Replace("\t"," ").Replace("\r"," ").Replace("\n"," ");
}
