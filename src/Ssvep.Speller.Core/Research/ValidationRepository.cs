using System.Text.Json;

namespace Ssvep.Speller.Core.Research;

public sealed record LocalValidation(string ProfileHash,string ProfileId,string ParticipantCode,string SessionId,string SourceFolder,
    string ManifestSha256,string RunConfigHash,DateTimeOffset RegisteredAt,string Status,EvaluationMetrics Algorithm,
    RecordedFeedbackReport? Feedback,string Conditions);

/// <summary>Verification is local evidence tied to a recorded, frozen profile; it is never imported as a profile flag.</summary>
public sealed class ValidationRepository(string directory)
{
    public LocalValidation Register(ResearchProfile profile,string folder,ReplayReport report,string participantCode,string conditions)
    {
        if(string.IsNullOrWhiteSpace(participantCode)||participantCode.Length>40||string.IsNullOrWhiteSpace(conditions)||conditions.Length>300)
            throw new ArgumentException("请填写参与者代号和设备/屏幕/佩戴条件（分别不超过 40、300 字）。");
        var reader=new SessionReader(folder); reader.VerifyFiles();
        if(reader.IsLegacy||!reader.Manifest.GetProperty("completed").GetBoolean()||reader.Warnings.Count>0)
            throw new InvalidOperationException("独立验证需要完整且文件指纹一致的新版本会话。");
        if(reader.Manifest.GetProperty("dataSource").GetString()!="synchroni-sdk")
            throw new InvalidOperationException("模拟数据不能授予个人验证状态。");
        if(reader.Context.Profile.ProfileHash!=profile.ProfileHash||report.ProfileHash!=profile.ProfileHash
            ||report.SourceSessionId!=reader.Manifest.GetProperty("sessionId").GetString()||report.Warnings.Count>0)
            throw new InvalidOperationException("只可登记使用相同冻结方案实际采集的独立会话，离线修改参数不算独立验证。");
        var plans=reader.Events("calibration-plan").ToArray();
        if(plans.Length!=1||plans[0].Data.GetProperty("purpose").GetString()!="independent-test"
            ||plans[0].Data.GetProperty("profileHash").GetString()!=profile.ProfileHash)
            throw new InvalidOperationException("请在新会话中选择“独立测试”采集；挑参记录不能登记为独立测试。");
        foreach(var path in Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*-training.json"):[])
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            if(doc.RootElement.GetProperty("profileHash").GetString()==profile.ProfileHash
                &&doc.RootElement.GetProperty("sourceSessionId").GetString()==report.SourceSessionId)
                throw new InvalidOperationException("此会话曾用于挑参，不能作为独立测试。");
        }
        var m=report.Metrics;
        bool sufficient=Enumerable.Range(1,6).All(t=>m.Trials.Count(v=>v.TargetId==$"T{t}"&&v.Status is "evaluated" or "no-valid-window")>=10)
            &&m.NoIntentMinutes>=10&&report.RecordedFeedback?.NoIntentEvaluableSeconds>=600;
        bool met=sufficient&&m.Accuracy.Value>=.95&&m.Coverage.Value>=.8&&m.FalseTriggersPerMinute<=.2
            &&report.RecordedFeedback?.FalseTriggersPerMinute.Value<=.2&&m.MedianDelaySeconds<=3;
        string status=!sufficient?"独立测试样本不足":met?"本次独立测试达到点估计目标":"本次独立测试未达目标";
        var evidence=new LocalValidation(profile.ProfileHash,profile.ProfileId,participantCode.Trim(),report.SourceSessionId,reader.Folder,
            CanonicalJson.BytesHash(File.ReadAllBytes(Path.Combine(folder,"manifest.json"))),reader.Context.RunConfigHash,DateTimeOffset.UtcNow,status,m,report.RecordedFeedback,conditions.Trim());
        string destination=Path.Combine(directory,"validation");Directory.CreateDirectory(destination);
        ProfileRepository.AtomicWrite(Path.Combine(destination,Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(evidence,CanonicalJson.Options),false);
        return evidence;
    }
    public LocalValidation? Latest(ResearchProfile profile)
    {
        string path=Path.Combine(directory,"validation");
        if(!Directory.Exists(path)) return null;
        return Directory.EnumerateFiles(path,"*.json").Select(file=>JsonSerializer.Deserialize<LocalValidation>(File.ReadAllText(file),CanonicalJson.Options)!)
            .Where(e=>e.ProfileId==profile.ProfileId&&e.ProfileHash==profile.ProfileHash).OrderByDescending(e=>e.RegisteredAt).FirstOrDefault();
    }
}
