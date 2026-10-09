using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Studio.App;

public sealed class ReplayPanel : UserControl
{
    private readonly StudioRuntime runtime;
    private readonly ResearchPanel profiles;
    private readonly TextBlock source=new() {Text="请选择包含 manifest.json 的实验记录目录。",Margin=new(0,0,0,16)};
    private readonly TextBlock status=new() {Text="无标签记录只显示得分与拒识；不计算正确率。",Margin=new(0,12,0,12),Foreground=Brushes.Teal};
    private readonly TextBox summary=new() {IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=180};
    private readonly ListBox candidates=new() {DisplayMemberPath="Description",MaxHeight=210};
    private readonly TextBox participant=new() {Width=150,MaxLength=40,ToolTip="参与者代号，例如 P01"};
    private readonly TextBox conditions=new() {MinWidth=250,MaxWidth=440,MaxLength=300,ToolTip="实际设备、显示器、佩戴和环境条件",TextWrapping=TextWrapping.Wrap};
    private string? folder;
    private ReplayReport? recorded;
    private ReplayReport? candidate;
    private ThresholdSearchResult? search;
    private ResearchProfile? evaluatedProfile;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? completion;
    private sealed record CandidateRow(ThresholdCandidate Candidate,string Description);
    public ReplayPanel(StudioRuntime runtime,ResearchPanel profiles)
    {
        this.runtime=runtime; this.profiles=profiles;
        var root=new StackPanel {Margin=new(20)};
        Content=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new Border {Background=Brushes.White,CornerRadius=new(10),Child=root}};
        root.Children.Add(source);
        var toolbar=new WrapPanel(); root.Children.Add(toolbar);
        Add(toolbar,"选择实验记录",Choose); Add(toolbar,"回放对比",()=>Run(Compare)); Add(toolbar,"扫描阈值",()=>Run(Search));
        Add(toolbar,"取消任务",()=>cancellation?.Cancel()); Add(toolbar,"导出报告",SaveReport); Add(toolbar,"导出 BrainVision",Export);
        root.Children.Add(status); root.Children.Add(summary); root.Children.Add(candidates);
        var actions=new WrapPanel {Margin=new(0,12,0,0)}; root.Children.Add(actions);
        Add(actions,"保存选中阈值为候选",SaveCandidate);
        var validationFields=new WrapPanel {Margin=new(0,12,0,0)};root.Children.Add(validationFields);
        validationFields.Children.Add(new TextBlock {Text="参与者代号",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,8,0)});validationFields.Children.Add(participant);
        validationFields.Children.Add(new TextBlock {Text="适用条件",VerticalAlignment=VerticalAlignment.Center,Margin=new(12,0,8,0)});validationFields.Children.Add(conditions);
        Add(validationFields,"登记独立测试记录",RegisterValidation);
        root.Children.Add(new TextBlock {Text="阈值排序只使用训练试次；留出试次仅用于检查。候选仍需新采数据独立验证。刺激频率改变必须重新采集。",Margin=new(0,14,0,0),Foreground=Brushes.SlateGray});
        Unloaded+=(_,_)=>cancellation?.Cancel();
    }
    private void Add(Panel panel,string caption,Action action)
    {
        var button=new Button {Content=caption,Margin=new(0,0,8,8)};
        button.Click+=(_,_)=> {try {action();} catch(Exception ex) {status.Text=ex.Message;} }; panel.Children.Add(button);
    }
    public void SetFolder(string path)
    {
        if(cancellation is not null) throw new InvalidOperationException("请等待当前任务结束。");
        var reader=new SessionReader(path); folder=reader.Folder; recorded=null; candidate=null; search=null; candidates.ItemsSource=null;
        source.Text=$"记录：{reader.Manifest.GetProperty("name").GetString()}\n{folder}";
        summary.Text=$"采样率：{reader.Context.SamplingRateHz} Hz\n参数：{reader.Context.Profile.Name}\n来源：{reader.Manifest.GetProperty("dataSource").GetString()}\n"+string.Join("\n",reader.Warnings);
    }
    private void Choose()
    {
        var dialog=new OpenFolderDialog {Title="选择实验记录目录",InitialDirectory=runtime.DataDirectory};
        if(dialog.ShowDialog(Window.GetWindow(this))==true) SetFolder(dialog.FolderName);
    }
    private async void Run(Func<CancellationToken,Task> work)
    {
        if(cancellation is not null) return;
        bool ownsWork=false;
        try
        {
            if(folder is null) throw new InvalidOperationException("请先选择实验记录。");
            runtime.BeginOfflineWork(); ownsWork=true; cancellation=new(); completion=new(TaskCreationOptions.RunContinuationsAsynchronously); status.Text="正在处理…";
            await work(cancellation.Token);
        }
        catch(OperationCanceledException) {status.Text="任务已取消，原始记录保持不变。";}
        catch(Exception ex) {status.Text="处理失败："+ex.Message;}
        finally {cancellation?.Dispose(); cancellation=null; if(ownsWork) runtime.EndOfflineWork(); completion?.TrySetResult();}
    }
    public Task CancelAsync() { cancellation?.Cancel(); return completion?.Task??Task.CompletedTask; }
    private async Task Compare(CancellationToken token)
    {
        recorded=null; candidate=null; search=null; candidates.ItemsSource=null;
        evaluatedProfile=runtime.Profile;
        var progress=new Progress<int>(n=>status.Text=$"已计算 {n} 个窗口…");
        recorded=await Task.Run(()=>ReplayRunner.Run(folder!,null,token,progress),token);
        candidate=recorded.ProfileHash==evaluatedProfile.ProfileHash?recorded:await Task.Run(()=>ReplayRunner.Run(folder!,evaluatedProfile,token,progress),token);
        summary.Text=Describe("记录参数",recorded)+"\n\n"+Describe("当前候选",candidate);
        status.Text="回放完成。所列延迟为样本时钟估计；尚未授予个人验证状态。";
        search=null; candidates.ItemsSource=null;
    }
    private async Task Search(CancellationToken token)
    {
        if(candidate is null||evaluatedProfile is null||candidate.ProfileHash!=runtime.Profile.ProfileHash) await Compare(token);
        var reader=new SessionReader(folder!);
        var events=reader.Events("trial-marker","trial-exclusion","calibration-plan").ToArray();
        search=await Task.Run(()=>ParameterSearch.Run(candidate!,events,reader.Context.SamplingRateHz,token),token);
        candidates.ItemsSource=search.Candidates.Select(c=>new CandidateRow(c,
            $"阈值 {c.Settings.MinScore:0.###} · {c.Settings.GapMode} 分差 {c.Settings.MinGap:0.###} | 训练正确 {Percent(c.Training.Accuracy.Value)} / 覆盖 {Percent(c.Training.Coverage.Value)} / 误触发 {Number(c.Training.FalseTriggersPerMinute)}/分 | 留出正确 {Percent(c.Holdout.Accuracy.Value)} / 覆盖 {Percent(c.Holdout.Coverage.Value)}")).ToArray();
        status.Text=$"扫描 {search.Candidates.Count} 组；训练 {search.TrainingTrials.Count} 个试次，留出 {search.HoldoutTrials.Count} 个试次。"+
            (search.Candidates.Any(c=>c.MeetsTrainingTargets)?"存在满足训练目标的候选，仍需独立测试。":"没有候选同时满足训练目标，请检查信号或采集新对照数据。");
    }
    private void SaveCandidate()
    {
        if(candidates.SelectedItem is not CandidateRow row||evaluatedProfile is null||search is null) throw new InvalidOperationException("请先扫描并选择阈值候选。");
        if(runtime.ConfigurationLocked) throw new InvalidOperationException("请等待实验或后台任务结束。");
        var saved=profiles.Repository.SaveRevision(evaluatedProfile with {ProfileId=Guid.NewGuid().ToString("N"),Name=evaluatedProfile.Name+" 阈值候选",Revision=1,Decision=row.Candidate.Settings});
        profiles.Load(saved);
        string evidence=Path.Combine(profiles.Repository.DirectoryPath,saved.ProfileId+"-training.json");
        File.WriteAllText(evidence,JsonSerializer.Serialize(new {saved.ProfileHash,search.SourceSessionId,search.TrainingTrials,search.HoldoutTrials,selected=row.Candidate},CanonicalJson.Options));
        status.Text="候选已保存且保持未验证；在参数页可设为下次实验方案。";
    }
    private void SaveReport()
    {
        if(candidate is null) throw new InvalidOperationException("请先完成回放。");
        var dialog=new SaveFileDialog {Title="导出回放报告",Filter="研发报告 JSON|*.json",FileName="SSVEP-回放报告.json"};
        if(dialog.ShowDialog(Window.GetWindow(this))==true)
            File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new {recorded,candidate,search},CanonicalJson.Options));
    }
    private void RegisterValidation()
    {
        if(candidate is null||evaluatedProfile is null) throw new InvalidOperationException("请先用该会话实际采集的方案完成回放。");
        string code=participant.Text,note=conditions.Text;var report=candidate;var profile=evaluatedProfile;
        Run(async token=>
        {
            var evidence=await Task.Run(()=>new ValidationRepository(profiles.Repository.DirectoryPath).Register(profile,folder!,report,code,note),token);
            profiles.RefreshValidation();status.Text=evidence.Status+"；仅适用于所记录的参与者及条件，置信区间见导出报告。";
        });
    }
    private void Export()
    {
        if(folder is null) throw new InvalidOperationException("请先选择实验记录。");
        var dialog=new OpenFolderDialog {Title="选择 BrainVision 导出父目录"};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true) return;
        string destination=dialog.FolderName;
        Run(async token=>
        {
            var result=await Task.Run(()=>BrainVisionExporter.Export(folder!,destination,token),token);
            status.Text=$"已导出 {result.SegmentCount} 个连续段、{result.SampleCount} 个样本。需用随包 MNE 脚本读回核验。\n{result.Directory}\n"+string.Join("\n",result.Warnings);
        });
    }
    private static string Percent(double? value)=>value is double n?$"{n:P1}":"缺少标签/有效试次";
    private static string Number(double? value)=>value is double n?$"{n:0.###}":"—";
    private static string Describe(string label,ReplayReport report)
    {
        var m=report.Metrics;
        return $"{label} · {report.AlgorithmVersion}\n状态：{m.Status}；窗口 {report.WindowCount}；评分单位 {report.ScoreVersion}\n"+
            $"首选正确率 {Percent(m.Accuracy.Value)}（{m.Accuracy.Numerator}/{m.Accuracy.Denominator}）；覆盖率 {Percent(m.Coverage.Value)}（{m.Coverage.Numerator}/{m.Coverage.Denominator}）\n"+
            $"无意图误触发 {Number(m.FalseTriggersPerMinute)} 次/分；有效时长 {m.NoIntentMinutes:0.00} 分钟；错误选择累计 {m.WrongSelectionSeconds:0.0} 秒\n"+
            $"正确首选延迟：中位数 {Number(m.MedianDelaySeconds)} 秒，P95 {Number(m.DelayP95Seconds)} 秒\n"+
            $"95% 区间：正确率 {Percent(m.Accuracy.Lower95)}–{Percent(m.Accuracy.Upper95)}；覆盖率 {Percent(m.Coverage.Lower95)}–{Percent(m.Coverage.Upper95)}；误触发 {Number(m.FalseTriggerInterval?.Lower95)}–{Number(m.FalseTriggerInterval?.Upper95)} 次/分\n"+
            (report.RecordedFeedback is not { } f?"":$"原记录在线反馈：{f.Status}（采集方案 {f.ProfileHash[..8]}）\n实际绘制误触发 {Number(f.FalseTriggersPerMinute.Value)} 次/分；继承框 {f.InheritedTriggers} 次；错误框 {f.WrongBorderSeconds:0.0} 秒，最长 {f.LongestWrongBorderSeconds:0.0} 秒\n判决 / 绘制延迟中位数 {Number(f.DecisionMedianSeconds)} / {Number(f.DrawMedianSeconds)} 秒；有效无意图 {f.NoIntentEvaluableSeconds:0.0}/{f.NoIntentTotalSeconds:0.0} 秒\n")+
            string.Join("\n",report.Warnings);
    }
}
