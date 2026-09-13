using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace OldenEraMods
{
    // End-to-end file-operation checks against disposable fixtures. Never targets the installed game.
    static class Smoke
    {
        public static void Run(string parent)
        {
            var root = Path.Combine(Path.GetFullPath(parent), "smoke-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root,"Launcher"); var game = Path.Combine(root,"Game"); var folder = Path.Combine(root,"Example");
            Directory.CreateDirectory(home); Directory.CreateDirectory(game); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(game,"HeroesOldenEra.exe"),"fixture");
            File.WriteAllText(Path.Combine(game,"GameAssembly.dll"),"supported version");
            File.WriteAllText(Path.Combine(game,"settings.txt"),"ORIGINAL");
            File.WriteAllText(Path.Combine(folder,"replacement.txt"),"MODDED");
            var mod = new Mod { id="example", name="Example", version="1", gameAssemblySha256=Disk.Hash(Path.Combine(game,"GameAssembly.dll")), files=new List<ModFile> {
                new ModFile {source="replacement.txt",target="settings.txt",sha256=Disk.Hash(Path.Combine(folder,"replacement.txt"))},
                new ModFile {source="replacement.txt",target="extra/new.txt",sha256=Disk.Hash(Path.Combine(folder,"replacement.txt"))}
            }};
            var path = Path.Combine(folder,"mod.json"); Disk.AtomicText(path,Disk.Json(mod));
            var manager = new Manager(home + Path.DirectorySeparatorChar,true); var passed = new List<string>();
            Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception("FAIL: "+name);passed.Add(name);};
            manager.Apply(game,new[]{"example"});
            check(File.ReadAllText(Path.Combine(game,"settings.txt"))=="MODDED" && File.Exists(Path.Combine(game,"extra/new.txt")),"Enable installs overlay and new file");
            manager.Apply(game,new[]{"example"}); check(manager.GetState().enabled.Count==1,"Enable is idempotent");
            manager.Apply(game,new string[0]);
            check(File.ReadAllText(Path.Combine(game,"settings.txt"))=="ORIGINAL" && !File.Exists(Path.Combine(game,"extra/new.txt")),"Disable restores original and removes only owned file");
            File.WriteAllText(Path.Combine(game,"GameAssembly.dll"),"different build");
            ExpectFailure(()=>manager.Apply(game,new[]{"example"})); check(File.ReadAllText(Path.Combine(game,"settings.txt"))=="ORIGINAL","Unsupported build refused without writes");
            File.WriteAllText(Path.Combine(game,"GameAssembly.dll"),"supported version");
            File.WriteAllText(Path.Combine(folder,"replacement.txt"),"CORRUPT");
            ExpectFailure(()=>manager.Apply(game,new[]{"example"})); check(!File.Exists(Path.Combine(game,"extra/new.txt")),"Corrupt payload refused");
            File.WriteAllText(Path.Combine(folder,"replacement.txt"),"MODDED");
            manager.Apply(game,new[]{"example"}); File.WriteAllText(Path.Combine(game,"settings.txt"),"EXTERNAL");
            ExpectFailure(()=>manager.Apply(game,new string[0])); check(File.ReadAllText(Path.Combine(game,"settings.txt"))=="EXTERNAL","External changes preserved");
            File.WriteAllText(Path.Combine(game,"settings.txt"),"MODDED"); manager.Apply(game,new string[0]);
            ExpectFailure(()=>Disk.SafePath(game,"../outside.txt")); ExpectFailure(()=>Disk.SafePath(game,"C:\\outside.txt")); ExpectFailure(()=>Disk.SafePath(game,"file:stream"));
            passed.Add("Traversal, absolute paths and NTFS alternate streams refused");
            var previous = manager.GetState();
            var snapshot="transactions\\fixture\\settings.bak"; Disk.AtomicCopy(Path.Combine(game,"settings.txt"),Disk.SafePath(home,snapshot));
            var journal = new Journal { gamePath=game, previous=previous, snapshots=new List<Snapshot> { new Snapshot {target="settings.txt",file=snapshot,beforeHash=Disk.Hash(Path.Combine(game,"settings.txt")),afterHash=Disk.Hash(Path.Combine(folder,"replacement.txt"))} } };
            Disk.AtomicText(Path.Combine(home,"pending-transaction.json"),Disk.Json(journal));
            File.Copy(Path.Combine(folder,"replacement.txt"),Path.Combine(game,"settings.txt"),true);
            manager.Apply(game,new string[0]);
            check(File.ReadAllText(Path.Combine(game,"settings.txt"))=="ORIGINAL" && !manager.HasPendingTransaction,"Interrupted transaction is recovered");
            Disk.AtomicText(Path.Combine(parent,"smoke-results.json"),Disk.Json(new{passed=passed,fixture=root,count=passed.Count}));
        }
        static void ExpectFailure(Action action) { bool failed=false; try {action();} catch(IOException){failed=true;} if(!failed)throw new Exception("Expected safe refusal"); }
    }
}
