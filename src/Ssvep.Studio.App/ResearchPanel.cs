using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Studio.App;

public sealed class ResearchPanel : UserControl
{
    private readonly StudioRuntime runtime;
    public ProfileRepository Repository { get; }
    private string? validationKey;
    private LocalValidation? validation;
    private ResearchProfile draft=ResearchProfile.Legacy();
    private readonly ComboBox saved=new() {MinWidth=260,DisplayMemberPath="Name"};
    private readonly TextBox name=new(),minScore=new(),minGap=new(),q=new();
    private readonly TextBox[] frequencies=Enumerable.Range(0,6).Select(_=>new TextBox {MinWidth=65,Margin=new(0,0,8,0)}).ToArray();
    private readonly TextBox[] lows=Enumerable.Range(0,3).Select(_=>new TextBox {Width=100}).ToArray();
    private readonly TextBox[] highs=Enumerable.Range(0,3).Select(_=>new TextBox {Width=100}).ToArray();
    private readonly TextBox[] weights=Enumerable.Range(0,3).Select(_=>new TextBox {Width=190}).ToArray();
    private readonly ComboBox gapMode=new() {ItemsSource=new[] {"相对分差","绝对分差"},MinWidth=130};
    private readonly ComboBox harmonics=new() {ItemsSource=new[] {"二次谐波","三次谐波"},MinWidth=130};
    private readonly ComboBox notch=new() {ItemsSource=new[] {"关闭软件陷波","50 Hz","60 Hz"},MinWidth=160};
    private readonly TextBlock status=new() {Margin=new(0,12,0,12),Foreground=Brushes.Teal};
    private readonly TextBlock applied=new() {Margin=new(0,0,0,14),FontWeight=FontWeights.SemiBold};
    private readonly StackPanel form=new();
    private readonly StackPanel actions=new() {Orientation=Orientation.Horizontal};
    private bool binding;
    public ResearchPanel(StudioRuntime runtime)
    {
        this.runtime=runtime;
        Repository=new(Path.Combine(Path.GetDirectoryName(runtime.DataDirectory)!,"Research","Profiles"));
        var root=new StackPanel {Margin=new(20)};
        Content=new ScrollViewer {Content=new Border {Background=Brushes.White,CornerRadius=new(10),Child=root},VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(applied);
        root.Children.Add(new TextBlock {Text="研发方案仅在新会话开始时生效。归一化分表示相关性，不是正确概率。",Margin=new(0,0,0,16)});
        var selection=new WrapPanel(); selection.Children.Add(saved);
        selection.Children.Add(Button("原版基线",()=>Load(ResearchProfile.Legacy())));
        selection.Children.Add(Button("新建研发候选",()=>Load(new ResearchProfile {TargetFrequenciesHz=draft.TargetFrequenciesHz.ToArray()})));
        selection.Children.Add(Button("导入参数",Import)); selection.Children.Add(Button("导出参数",Export)); root.Children.Add(selection);
        root.Children.Add(form);
        form.Children.Add(Label("方案名称",name));
        form.Children.Add(new TextBlock {Text="六目标刺激频率（Hz）",Margin=new(0,16,0,8)});
        var frequencyRow=new WrapPanel(); for(int i=0;i<6;i++) { var column=new StackPanel(); column.Children.Add(new TextBlock {Text=$"目标 {i+1}"}); column.Children.Add(frequencies[i]); frequencyRow.Children.Add(column); } form.Children.Add(frequencyRow);
        var thresholds=new WrapPanel {Margin=new(0,12,0,0)};
        minScore.Width=130; minGap.Width=130;
        thresholds.Children.Add(Label("最高分阈值",minScore)); thresholds.Children.Add(Label("分差模式",gapMode)); thresholds.Children.Add(Label("最小分差",minGap)); form.Children.Add(thresholds);
        form.Children.Add(new TextBlock {Text="软件滤波与子带权重",Margin=new(0,16,0,8),FontWeight=FontWeights.SemiBold});
        for(int i=0;i<3;i++)
        {
            var row=new WrapPanel(); row.Children.Add(new TextBlock {Text=$"子带 {i+1}",Width=60,VerticalAlignment=VerticalAlignment.Center});
            row.Children.Add(Label("下限 Hz",lows[i])); row.Children.Add(Label("上限 Hz",highs[i])); row.Children.Add(Label("权重",weights[i])); form.Children.Add(row);
        }
        var extras=new WrapPanel(); q.Width=110;
        extras.Children.Add(Label("参考谐波",harmonics)); extras.Children.Add(Label("软件陷波",notch)); extras.Children.Add(Label("陷波 Q",q)); form.Children.Add(extras);
        root.Children.Add(status); root.Children.Add(actions);
        actions.Children.Add(Button("保存新修订",()=> { var next=Repository.SaveRevision(Read()); Load(next); RefreshList(); status.Text="修订已保存；点击“设为下次方案”后应用。"; }));
        actions.Children.Add(Button("设为下次方案",()=> { var next=Read(); next.Validate(runtime.IsConnected?runtime.SamplingRate:500); Repository.SetDefault(next); runtime.ApplyProfile(next); status.Text="下次实验将使用此修订；验证状态见上方。"; }));
        saved.SelectionChanged+=(_,_)=> { if(!binding&&saved.SelectedItem is ResearchProfile item) Load(item); };
        foreach(var box in frequencies.Concat(lows).Concat(highs).Concat(weights).Concat(new[] {name,minScore,minGap,q})) box.TextChanged+=(_,_)=>ValidateDraft();
        foreach(var combo in new[] {gapMode,harmonics,notch}) combo.SelectionChanged+=(_,_)=>ValidateDraft();
        try { runtime.ApplyProfile(Repository.LoadDefault()); RefreshList(); Load(runtime.Profile); }
        catch(Exception ex) { Load(ResearchProfile.Legacy()); status.Text="参数仓库读取失败，使用内置基线："+ex.Message; }
    }

    private static FrameworkElement Label(string text,FrameworkElement field)
    {
        var panel=new StackPanel {Margin=new(0,6,14,6)}; panel.Children.Add(new TextBlock {Text=text,Margin=new(0,0,0,6)}); panel.Children.Add(field); return panel;
    }
    private Button Button(string text,Action action)
    {
        var button=new Button {Content=text,Margin=new(8,0,0,8)};
        button.Click+=(_,_)=> { try { if(runtime.ConfigurationLocked) throw new InvalidOperationException("请结束实验后修改参数。"); action(); } catch(Exception ex) { status.Text=ex.Message; status.Foreground=Brushes.DarkOrange; } };
        return button;
    }
    public void RefreshState()
    {
        IsEnabled=!runtime.ConfigurationLocked;
        string key=runtime.Profile.ProfileId+runtime.Profile.ProfileHash;
        if(key!=validationKey)
        {
            validationKey=key;
            try {validation=new ValidationRepository(Repository.DirectoryPath).Latest(runtime.Profile);} catch {validation=null;}
        }
        string validationText=validation is null?"参数未作个人验证":runtime.ActiveContext is { } context&&context.RunConfigHash!=validation.RunConfigHash
            ?"适用条件已改变，需要复核":validation.Status+"（仅限记录条件）";
        applied.Text=$"下次实验：{runtime.Profile.Name} · 修订 {runtime.Profile.Revision} · {(runtime.Profile.IsLegacy?"原始评分":"归一化评分")} · {validationText}";
    }
    public void RefreshValidation() {validationKey=null;RefreshState();}
    private void RefreshList()
    {
        binding=true; saved.ItemsSource=Repository.List().OrderByDescending(p=>p.Revision).ToArray(); binding=false;
    }
    public void Load(ResearchProfile profile)
    {
        binding=true; draft=profile.Freeze();
        name.Text=draft.Name; minScore.Text=Number(draft.Decision.MinScore); minGap.Text=Number(draft.Decision.MinGap); gapMode.SelectedIndex=draft.Decision.GapMode=="relative"?0:1;
        for(int i=0;i<6;i++) frequencies[i].Text=Number(draft.TargetFrequenciesHz[i]);
        for(int i=0;i<3;i++) { lows[i].Text=Number(draft.Filter.BandsHz[i][0]); highs[i].Text=Number(draft.Filter.BandsHz[i][1]); weights[i].Text=Number(draft.BandWeights[i]); }
        harmonics.SelectedIndex=draft.HarmonicCount-2; notch.SelectedIndex=draft.Filter.SoftwareNotchHz is null?0:draft.Filter.SoftwareNotchHz==50?1:2; q.Text=Number(draft.Filter.NotchQ);
        form.IsEnabled=!draft.IsLegacy; binding=false; ValidateDraft(); RefreshState();
    }
    private static string Number(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
    private static double Parse(TextBox field)
    {
        if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value)) throw new ArgumentException("请输入有效数字，使用小数点“.”。");
        return value;
    }
    public ResearchProfile Read()
    {
        if(draft.IsLegacy) return draft;
        return (draft with
        {
            Name=name.Text.Trim(),TargetFrequenciesHz=frequencies.Select(Parse).ToArray(),HarmonicCount=harmonics.SelectedIndex+2,
            BandWeights=weights.Select(Parse).ToArray(),Decision=new(Parse(minScore),gapMode.SelectedIndex==0?"relative":"absolute",Parse(minGap)),
            Filter=new() {BandsHz=Enumerable.Range(0,3).Select(i=>(IReadOnlyList<double>)new[] {Parse(lows[i]),Parse(highs[i])}).ToArray(),
                SoftwareNotchHz=notch.SelectedIndex==0?null:notch.SelectedIndex==1?50:60,NotchQ=Parse(q)}
        }).Freeze();
    }
    private void ValidateDraft()
    {
        if(binding) return;
        try
        {
            var current=Read(); var warnings=current.Validate(runtime.SamplingRate);
            status.Text=(current.IsLegacy?"内置基线只读。":"参数有效，尚未做个人验证。")+"\n"+string.Join("\n",warnings)+$"\n配置指纹：{current.ProfileHash[..16]}…";
            status.Foreground=Brushes.Teal;
        }
        catch(Exception ex) { status.Text=ex.Message; status.Foreground=Brushes.DarkOrange; }
    }
    private void Import()
    {
        var dialog=new OpenFileDialog {Filter="研发参数|*.ssvep-research.json;*.json",Title="导入研发方案"};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true) return;
        var imported=ResearchProfile.Parse(File.ReadAllText(dialog.FileName));
        Load(imported.IsLegacy?imported:imported with {ProfileId=Guid.NewGuid().ToString("N"),Revision=1});
        status.Text="已载入草稿。导入不会改变默认方案，也不会授予已验证状态。";
    }
    private void Export()
    {
        var next=Read(); var dialog=new SaveFileDialog {Filter="研发参数|*.ssvep-research.json",FileName="研发方案.ssvep-research.json"};
        if(dialog.ShowDialog(Window.GetWindow(this))==true) File.WriteAllText(dialog.FileName,next.ToJson());
    }
}
