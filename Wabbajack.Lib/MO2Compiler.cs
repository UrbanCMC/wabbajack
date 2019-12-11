﻿using Compression.BSA;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Lib.Validation;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Game = Wabbajack.Common.Game;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Lib
{
    public class MO2Compiler : ACompiler
    {

        private string _mo2DownloadsFolder;
        
        public string MO2Folder;

        public string MO2Profile { get; }
        public Dictionary<string, dynamic> ModMetas { get; set; }

        public override ModManager ModManager => ModManager.MO2;

        public override string GamePath { get; }

        public GameMetaData CompilingGame { get; set; }

        public override string ModListOutputFolder => "output_folder";

        public override string ModListOutputFile { get; }

        public MO2Compiler(string mo2Folder, string mo2Profile, string outputFile)
        {
            MO2Folder = mo2Folder;
            MO2Profile = mo2Profile;
            MO2Ini = Path.Combine(MO2Folder, "ModOrganizer.ini").LoadIniFile();
            var mo2game = (string)MO2Ini.General.gameName;
            CompilingGame = GameRegistry.Games.First(g => g.Value.MO2Name == mo2game).Value;
            GamePath = ((string)MO2Ini.General.gamePath).Replace("\\\\", "\\");
            ModListOutputFile = outputFile;
        }

        public dynamic MO2Ini { get; }

        public bool IgnoreMissingFiles { get; set; }

        public string MO2DownloadsFolder
        {
            get
            {
                if (_mo2DownloadsFolder != null) return _mo2DownloadsFolder;
                if (MO2Ini != null)
                    if (MO2Ini.Settings != null)
                        if (MO2Ini.Settings.download_directory != null)
                            return MO2Ini.Settings.download_directory.Replace("/", "\\");
                return GetTypicalDownloadsFolder(MO2Folder);
            }
            set => _mo2DownloadsFolder = value;
        }

        public static string GetTypicalDownloadsFolder(string mo2Folder) => Path.Combine(mo2Folder, "downloads");

        public string MO2ProfileDir => Path.Combine(MO2Folder, "profiles", MO2Profile);

        internal UserStatus User { get; private set; }
        public ConcurrentBag<Directive> ExtraFiles { get; private set; }
        public Dictionary<string, dynamic> ModInis { get; private set; }

        public HashSet<string> SelectedProfiles { get; set; } = new HashSet<string>();

        protected override bool _Begin()
        {
            ConfigureProcessor(18);
            UpdateTracker.Reset();
            UpdateTracker.NextStep("Gathering information");
            Info("Looking for other profiles");
            var otherProfilesPath = Path.Combine(MO2ProfileDir, "otherprofiles.txt");
            SelectedProfiles = new HashSet<string>();
            if (File.Exists(otherProfilesPath)) SelectedProfiles = File.ReadAllLines(otherProfilesPath).ToHashSet();
            SelectedProfiles.Add(MO2Profile);

            Info("Using Profiles: " + string.Join(", ", SelectedProfiles.OrderBy(p => p)));

            VFS.IntegrateFromFile(_vfsCacheName);

            var roots = new List<string>()
            {
                MO2Folder, GamePath, MO2DownloadsFolder
            };
            
            // TODO: make this generic so we can add more paths

            var lootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LOOT");
            IEnumerable<RawSourceFile> lootFiles = new List<RawSourceFile>();
            if (Directory.Exists(lootPath))
            {
                roots.Add(lootPath);
            }
            UpdateTracker.NextStep("Indexing folders");

            VFS.AddRoots(roots);
            VFS.WriteToFile(_vfsCacheName);
            
            if (Directory.Exists(lootPath))
            {
                    lootFiles = Directory.EnumerateFiles(lootPath, "userlist.yaml", SearchOption.AllDirectories)
                    .Where(p => p.FileExists())
                    .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                        { Path = Path.Combine(Consts.LOOTFolderFilesDir, p.RelativeTo(lootPath)) });
            }

            UpdateTracker.NextStep("Cleaning output folder");
            if (Directory.Exists(ModListOutputFolder))
                Utils.DeleteDirectory(ModListOutputFolder);

            UpdateTracker.NextStep("Inferring metas for game file downloads");
            InferMetas();

            UpdateTracker.NextStep("Reindexing downloads after meta inferring");
            VFS.AddRoot(MO2DownloadsFolder);
            VFS.WriteToFile(_vfsCacheName);

            UpdateTracker.NextStep("Finding Install Files");
            Directory.CreateDirectory(ModListOutputFolder);

            var mo2Files = Directory.EnumerateFiles(MO2Folder, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p]) { Path = p.RelativeTo(MO2Folder) });

            var gameFiles = Directory.EnumerateFiles(GamePath, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                { Path = Path.Combine(Consts.GameFolderFilesDir, p.RelativeTo(GamePath)) });




            IndexedArchives = Directory.EnumerateFiles(MO2DownloadsFolder)
                .Where(f => File.Exists(f + ".meta"))
                .Select(f => new IndexedArchive
                {
                    File = VFS.Index.ByRootPath[f],
                    Name = Path.GetFileName(f),
                    IniData = (f + ".meta").LoadIniFile(),
                    Meta = File.ReadAllText(f + ".meta")
                })
                .ToList();

            ModMetas = Directory.EnumerateDirectories(Path.Combine(MO2Folder, "mods"))
                .Keep(f =>
                {
                    var path = Path.Combine(f, "meta.ini");
                    return File.Exists(path) ? (f, path.LoadIniFile()) : default;
                }).ToDictionary(f => f.f.RelativeTo(MO2Folder) + "\\", v => v.Item2);

            IndexedFiles = IndexedArchives.SelectMany(f => f.File.ThisAndAllChildren)
                .OrderBy(f => f.NestingFactor)
                .GroupBy(f => f.Hash)
                .ToDictionary(f => f.Key, f => f.AsEnumerable());

            AllFiles = mo2Files.Concat(gameFiles)
                .Concat(lootFiles)
                .DistinctBy(f => f.Path)
                .ToList();

            Info($"Found {AllFiles.Count} files to build into mod list");

            UpdateTracker.NextStep("Verifying destinations");

            var dups = AllFiles.GroupBy(f => f.Path)
                .Where(fs => fs.Count() > 1)
                .Select(fs =>
                {
                    Utils.Log($"Duplicate files installed to {fs.Key} from : {String.Join(", ", fs.Select(f => f.AbsolutePath))}");
                    return fs;
                }).ToList();

            if (dups.Count > 0)
            {
                Error($"Found {dups.Count} duplicates, exiting");
            }

            ExtraFiles = new ConcurrentBag<Directive>();


            UpdateTracker.NextStep("Loading INIs");

            ModInis = Directory.EnumerateDirectories(Path.Combine(MO2Folder, "mods"))
                .Select(f =>
                {
                    var modName = Path.GetFileName(f);
                    var metaPath = Path.Combine(f, "meta.ini");
                    if (File.Exists(metaPath))
                        return (mod_name: modName, metaPath.LoadIniFile());
                    return (null, null);
                })
                .Where(f => f.Item2 != null)
                .ToDictionary(f => f.Item1, f => f.Item2);

            var stack = MakeStack();

            UpdateTracker.NextStep("Running Compilation Stack");
            var results = AllFiles.PMap(Queue, UpdateTracker, f => RunStack(stack, f)).ToList();

            // Add the extra files that were generated by the stack

            UpdateTracker.NextStep($"Adding {ExtraFiles.Count} that were generated by the stack");
            results = results.Concat(ExtraFiles).ToList();

            var nomatch = results.OfType<NoMatch>();
            Info($"No match for {nomatch.Count()} files");
            foreach (var file in nomatch)
                Info($"     {file.To}");
            if (nomatch.Count() > 0)
            {
                if (IgnoreMissingFiles)
                {
                    Info("Continuing even though files were missing at the request of the user.");
                }
                else
                {
                    Info("Exiting due to no way to compile these files");
                    return false;
                }
            }

            InstallDirectives = results.Where(i => !(i is IgnoredDirectly)).ToList();

            Info("Getting Nexus api_key, please click authorize if a browser window appears");

            if (IndexedArchives.Any(a => a.IniData?.General?.gameName != null))
            {
                var nexusClient = new NexusApiClient();
                if (!nexusClient.IsPremium) Error($"User {nexusClient.Username} is not a premium Nexus user, so we cannot access the necessary API calls, cannot continue");

            }

            UpdateTracker.NextStep("Verifying Files");
            zEditIntegration.VerifyMerges(this);


            UpdateTracker.NextStep("Gathering Archives");
            GatherArchives();
            UpdateTracker.NextStep("Including Archive Metadata");
            IncludeArchiveMetadata();
            UpdateTracker.NextStep("Building Patches");
            BuildPatches();

            ModList = new ModList
            {
                GameType = CompilingGame.Game,
                WabbajackVersion = WabbajackVersion,
                Archives = SelectedArchives.ToList(),
                ModManager = ModManager.MO2,
                Directives = InstallDirectives,
                Name = ModListName ?? MO2Profile,
                Author = ModListAuthor ?? "",
                Description = ModListDescription ?? "",
                Readme = ModListReadme ?? "",
                Image = ModListImage ?? "",
                Website = ModListWebsite ?? ""
            };

            UpdateTracker.NextStep("Running Validation");

            ValidateModlist.RunValidation(ModList);
            UpdateTracker.NextStep("Generating Report");

            GenerateReport();

            UpdateTracker.NextStep("Exporting Modlist");
            ExportModList();

            ResetMembers();

            ShowReport();

            UpdateTracker.NextStep("Done Building Modlist");

            return true;
        }
        private void InferMetas()
        {
            var to_find = Directory.EnumerateFiles(MO2DownloadsFolder)
                .Where(f => !f.EndsWith(".meta") && !f.EndsWith(Consts.HashFileExtension))
                .Where(f => !File.Exists(f + ".meta"))
                .ToList();

            if (to_find.Count == 0) return;

            var games = new[]{CompilingGame}.Concat(GameRegistry.Games.Values.Where(g => g != CompilingGame));
            var game_files = games
                .Where(g => g.GameLocation() != null)
                .SelectMany(game => Directory.EnumerateFiles(game.GameLocation(), "*", DirectoryEnumerationOptions.Recursive).Select(name => (game, name)))
                .GroupBy(f => (Path.GetFileName(f.name), new FileInfo(f.name).Length))
                .ToDictionary(f => f.Key);

            to_find.PMap(Queue, f =>
            {
                var vf = VFS.Index.ByFullPath[f];
                if (!game_files.TryGetValue((Path.GetFileName(f), vf.Size), out var found))
                    return;

                var (game, name) = found.FirstOrDefault(ff => ff.name.FileHash() == vf.Hash);
                if (name == null)
                    return;

                File.WriteAllLines(f+".meta", new[]
                {
                    "[General]",
                    $"gameName={game.MO2ArchiveName}",
                    $"gameFile={name.RelativeTo(game.GameLocation()).Replace("\\", "/")}"
                });
            });

        }


        private void IncludeArchiveMetadata()
        {
            Utils.Log($"Including {SelectedArchives.Count} .meta files for downloads");
            SelectedArchives.Do(a =>
            {
                var source = Path.Combine(MO2DownloadsFolder, a.Name + ".meta");
                InstallDirectives.Add(new ArchiveMeta()
                {
                    SourceDataID = IncludeFile(File.ReadAllText(source)),
                    Size = File.GetSize(source),
                    Hash = source.FileHash(),
                    To = Path.GetFileName(source)
                });
            });
        }

        /// <summary>
        ///     Clear references to lists that hold a lot of data.
        /// </summary>
        private void ResetMembers()
        {
            AllFiles = null;
            InstallDirectives = null;
            SelectedArchives = null;
            ExtraFiles = null;
        }


        /// <summary>
        ///     Fills in the Patch fields in files that require them
        /// </summary>
        private void BuildPatches()
        {
            Info("Gathering patch files");

            InstallDirectives.OfType<PatchedFromArchive>()
                .Where(p => p.PatchID == null)
                .Do(p =>
                {
                    if (Utils.TryGetPatch(p.FromHash, p.Hash, out var bytes))
                        p.PatchID = IncludeFile(bytes);
                });

            var groups = InstallDirectives.OfType<PatchedFromArchive>()
                .Where(p => p.PatchID == null)
                .GroupBy(p => p.ArchiveHashPath[0])
                .ToList();

            Info($"Patching building patches from {groups.Count} archives");
            var absolutePaths = AllFiles.ToDictionary(e => e.Path, e => e.AbsolutePath);
            groups.PMap(Queue, group => BuildArchivePatches(group.Key, group, absolutePaths));

            if (InstallDirectives.OfType<PatchedFromArchive>().FirstOrDefault(f => f.PatchID == null) != null)
                Error("Missing patches after generation, this should not happen");
        }

        private void BuildArchivePatches(string archiveSha, IEnumerable<PatchedFromArchive> group,
            Dictionary<string, string> absolutePaths)
        {
            using (var files = VFS.StageWith(group.Select(g => VFS.Index.FileForArchiveHashPath(g.ArchiveHashPath))))
            {
                var byPath = files.GroupBy(f => string.Join("|", f.FilesInFullPath.Skip(1).Select(i => i.Name)))
                    .ToDictionary(f => f.Key, f => f.First());
                // Now Create the patches
                group.PMap(Queue, entry =>
                {
                    Info($"Patching {entry.To}");
                    Status($"Patching {entry.To}");
                    using (var origin = byPath[string.Join("|", entry.ArchiveHashPath.Skip(1))].OpenRead())
                    using (var output = new MemoryStream())
                    {
                        var a = origin.ReadAll();
                        var b = LoadDataForTo(entry.To, absolutePaths);
                        Utils.CreatePatch(a, b, output);
                        entry.PatchID = IncludeFile(output.ToArray());
                        var fileSize = File.GetSize(Path.Combine(ModListOutputFolder, entry.PatchID));
                        Info($"Patch size {fileSize} for {entry.To}");
                    }
                });
            }
        }

        private byte[] LoadDataForTo(string to, Dictionary<string, string> absolutePaths)
        {
            if (absolutePaths.TryGetValue(to, out var absolute))
                return File.ReadAllBytes(absolute);

            if (to.StartsWith(Consts.BSACreationDir))
            {
                var bsaID = to.Split('\\')[1];
                var bsa = InstallDirectives.OfType<CreateBSA>().First(b => b.TempID == bsaID);

                using (var a = BSADispatch.OpenRead(Path.Combine(MO2Folder, bsa.To)))
                {
                    var find = Path.Combine(to.Split('\\').Skip(2).ToArray());
                    var file = a.Files.First(e => e.Path.Replace('/', '\\') == find);
                    using (var ms = new MemoryStream())
                    {
                        file.CopyDataTo(ms);
                        return ms.ToArray();
                    }
                }
            }

            Error($"Couldn't load data for {to}");
            return null;
        }

        public override IEnumerable<ICompilationStep> GetStack()
        {
            var userConfig = Path.Combine(MO2ProfileDir, "compilation_stack.yml");
            if (File.Exists(userConfig))
                return Serialization.Deserialize(File.ReadAllText(userConfig), this);

            var stack = MakeStack();

            File.WriteAllText(Path.Combine(MO2ProfileDir, "_current_compilation_stack.yml"), 
                Serialization.Serialize(stack));

            return stack;

        }

        /// <summary>
        ///     Creates a execution stack. The stack should be passed into Run stack. Each function
        ///     in this stack will be run in-order and the first to return a non-null result will have its
        ///     result included into the pack
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<ICompilationStep> MakeStack()
        {
            Utils.Log("Generating compilation stack");
            return new List<ICompilationStep>
            {
                new IncludePropertyFiles(this),
                new IgnoreStartsWith(this,"logs\\"),
                new IgnoreStartsWith(this, "downloads\\"),
                new IgnoreStartsWith(this,"webcache\\"),
                new IgnoreStartsWith(this, "overwrite\\"),
                new IgnorePathContains(this,"temporary_logs"),
                new IgnorePathContains(this, "GPUCache"),
                new IgnorePathContains(this, "SSEEdit Cache"),
                new IgnoreEndsWith(this, ".pyc"),
                new IgnoreEndsWith(this, ".log"),
                new IgnoreOtherProfiles(this),
                new IgnoreDisabledMods(this),
                new IncludeThisProfile(this),
                // Ignore the ModOrganizer.ini file it contains info created by MO2 on startup
                new IncludeStubbedConfigFiles(this),
                new IncludeLootFiles(this),
                new IgnoreStartsWith(this, Path.Combine(Consts.GameFolderFilesDir, "Data")),
                new IgnoreStartsWith(this, Path.Combine(Consts.GameFolderFilesDir, "Papyrus Compiler")),
                new IgnoreStartsWith(this, Path.Combine(Consts.GameFolderFilesDir, "Skyrim")),
                new IgnoreRegex(this, Consts.GameFolderFilesDir + "\\\\.*\\.bsa"),
                new IncludeModIniData(this),
                new DirectMatch(this),
                new IncludeTaggedMods(this, Consts.WABBAJACK_INCLUDE),
                new DeconstructBSAs(this), // Deconstruct BSAs before building patches so we don't generate massive patch files
                new IncludePatches(this),
                new IncludeDummyESPs(this),


                // If we have no match at this point for a game folder file, skip them, we can't do anything about them
                new IgnoreGameFiles(this),

                // There are some types of files that will error the compilation, because they're created on-the-fly via tools
                // so if we don't have a match by this point, just drop them.
                new IgnoreEndsWith(this, ".ini"),
                new IgnoreEndsWith(this, ".html"),
                new IgnoreEndsWith(this, ".txt"),
                // Don't know why, but this seems to get copied around a bit
                new IgnoreEndsWith(this, "HavokBehaviorPostProcess.exe"),
                // Theme file MO2 downloads somehow
                new IgnoreEndsWith(this, "splash.png"),

                new IgnoreEndsWith(this, ".bin"),
                new IgnoreEndsWith(this, ".refcache"),

                new IgnoreWabbajackInstallCruft(this),

                new PatchStockESMs(this),

                new IncludeAllConfigs(this),
                new zEditIntegration.IncludeZEditPatches(this),
                new IncludeTaggedMods(this, Consts.WABBAJACK_NOMATCH_INCLUDE),

                new DropAll(this)
            };
        }

        public class IndexedFileMatch
        {
            public IndexedArchive Archive;
            public IndexedArchiveEntry Entry;
            public DateTime LastModified;
        }
    }
}
