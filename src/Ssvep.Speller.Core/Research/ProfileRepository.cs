using System.Text.Json;

namespace Ssvep.Speller.Core.Research;

public sealed class ProfileRepository(string directory)
{
    public string DirectoryPath => directory;
    public IReadOnlyList<ResearchProfile> List()
    {
        if(!Directory.Exists(directory)) return [ResearchProfile.Legacy()];
        var values=new List<ResearchProfile> {ResearchProfile.Legacy()};
        foreach(var file in Directory.EnumerateFiles(directory,"*.ssvep-research.json").Order())
            values.Add(ResearchProfile.Parse(File.ReadAllText(file)));
        return values;
    }
    public ResearchProfile SaveRevision(ResearchProfile profile)
    {
        profile.Validate();
        if(profile.IsLegacy) throw new InvalidOperationException("内置基线只读。");
        Directory.CreateDirectory(directory);
        int revision=List().Where(p=>p.ProfileId==profile.ProfileId).Select(p=>p.Revision).DefaultIfEmpty(0).Max()+1;
        var next=(profile with {Revision=revision}).Freeze();
        string path=Path.Combine(directory,$"{CanonicalJson.BytesHash(System.Text.Encoding.UTF8.GetBytes(next.ProfileId))}-{revision}.ssvep-research.json");
        AtomicWrite(path,next.ToJson(),false);
        return next;
    }
    public void SetDefault(ResearchProfile profile)
    {
        var saved=List().SingleOrDefault(p=>p.ProfileId==profile.ProfileId&&p.Revision==profile.Revision&&p.ProfileHash==profile.ProfileHash)
            ??throw new InvalidOperationException("请先保存参数修订。");
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory,"default.json"),JsonSerializer.Serialize(new {saved.ProfileId,saved.Revision,saved.ProfileHash},CanonicalJson.Options),true);
    }
    public ResearchProfile LoadDefault()
    {
        string path=Path.Combine(directory,"default.json");
        if(!File.Exists(path)) return ResearchProfile.Legacy().Freeze();
        using var json=JsonDocument.Parse(File.ReadAllText(path)); var item=json.RootElement;
        return List().SingleOrDefault(p=>p.ProfileId==item.GetProperty("profileId").GetString()&&p.Revision==item.GetProperty("revision").GetInt32()
            &&p.ProfileHash==item.GetProperty("profileHash").GetString())??throw new InvalidDataException("默认参数修订不存在或指纹不匹配。");
    }
    public static void AtomicWrite(string path,string text,bool overwrite)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temp,text); File.Move(temp,path,overwrite); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
