﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.WindowsAPICodePack.Shell;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.ModListRegistry;
using Wabbajack.Lib.NexusApi;
using Wabbajack.VirtualFileSystem;
using File = Alphaleonis.Win32.Filesystem.File;

namespace Wabbajack.Lib
{
    public class VortexCompiler : ACompiler
    {
        public Game Game { get; }
        public string GameName { get; }

        public string VortexFolder { get; set; }
        public string StagingFolder { get; set; }
        public string DownloadsFolder { get; set; }

        public bool IgnoreMissingFiles { get; set; }

        public const string StagingMarkerName = "__vortex_staging_folder";
        public const string DownloadMarkerName = "__vortex_downloads_folder";

        public VortexCompiler(Game game, string gamePath, string vortexFolder, string downloadsFolder, string stagingFolder)
        {
            UpdateTracker = new StatusUpdateTracker(10);
            VFS = new Context(Queue) {UpdateTracker = UpdateTracker};

            ModManager = ModManager.Vortex;
            Game = game;

            // TODO: only for testing
            IgnoreMissingFiles = true;

            GamePath = gamePath;
            GameName = GameRegistry.Games[game].NexusName;
            VortexFolder = vortexFolder;
            DownloadsFolder = downloadsFolder;
            StagingFolder = stagingFolder;
            ModListOutputFolder = "output_folder";

            // TODO: add custom modlist name
            ModListOutputFile = $"VORTEX_TEST_MODLIST{ExtensionManager.Extension}";
        }

        public override void Info(string msg)
        {
            Utils.Log(msg);
        }

        public override void Status(string msg)
        {
            Queue.Report(msg, 0);
        }

        public override void Error(string msg)
        {
            Utils.Log(msg);
            throw new Exception(msg);
        }

        internal override string IncludeFile(byte[] data)
        {
            var id = Guid.NewGuid().ToString();
            File.WriteAllBytes(Path.Combine(ModListOutputFolder, id), data);
            return id;
        }

        internal override string IncludeFile(string data)
        {
            var id = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(ModListOutputFolder, id), data);
            return id;
        }

        public override bool Compile()
        {
            Info($"Starting Vortex compilation for {GameName} at {GamePath} with staging folder at {StagingFolder} and downloads folder at  {DownloadsFolder}.");

            Info("Starting pre-compilation steps");
            CreateMetaFiles();

            Info($"Indexing {StagingFolder}");
            VFS.AddRoot(StagingFolder);

            Info($"Indexing {GamePath}");
            VFS.AddRoot(GamePath);

            Info($"Indexing {DownloadsFolder}");
            VFS.AddRoot(DownloadsFolder);

            AddExternalFolder();

            Info("Cleaning output folder");
            if (Directory.Exists(ModListOutputFolder)) Directory.Delete(ModListOutputFolder, true);
            Directory.CreateDirectory(ModListOutputFolder);
            
            IEnumerable<RawSourceFile> vortexStagingFiles = Directory.EnumerateFiles(StagingFolder, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists() && p != StagingMarkerName)
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    {Path = p.RelativeTo(StagingFolder)});
            
            IEnumerable<RawSourceFile> vortexDownloads = Directory.EnumerateFiles(DownloadsFolder, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    {Path = p.RelativeTo(DownloadsFolder)});

            IEnumerable<RawSourceFile> gameFiles = Directory.EnumerateFiles(GamePath, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    { Path = Path.Combine(Consts.GameFolderFilesDir, p.RelativeTo(GamePath)) });

            Info("Indexing Archives");
            IndexedArchives = Directory.EnumerateFiles(DownloadsFolder)
                .Where(f => File.Exists(f + ".meta"))
                .Select(f => new IndexedArchive
                {
                    File = VFS.Index.ByRootPath[f],
                    Name = Path.GetFileName(f),
                    IniData = (f + ".meta").LoadIniFile(),
                    Meta = File.ReadAllText(f + ".meta")
                })
                .ToList();

            Info("Indexing Files");
            IndexedFiles = IndexedArchives.SelectMany(f => f.File.ThisAndAllChildren)
                .OrderBy(f => f.NestingFactor)
                .GroupBy(f => f.Hash)
                .ToDictionary(f => f.Key, f => f.AsEnumerable());

            Info("Searching for mod files");
            AllFiles = vortexStagingFiles.Concat(vortexDownloads)
                .Concat(gameFiles)
                .DistinctBy(f => f.Path)
                .ToList();

            Info($"Found {AllFiles.Count} files to build into mod list");

            Info("Verifying destinations");
            List<IGrouping<string, RawSourceFile>> dups = AllFiles.GroupBy(f => f.Path)
                .Where(fs => fs.Count() > 1)
                .Select(fs =>
                {
                    Utils.Log($"Duplicate files installed to {fs.Key} from : {string.Join(", ", fs.Select(f => f.AbsolutePath))}");
                    return fs;
                }).ToList();

            if (dups.Count > 0)
            {
                Error($"Found {dups.Count} duplicates, exiting");
            }

            IEnumerable<ICompilationStep> stack = MakeStack();

            Info("Running Compilation Stack");
            List<Directive> results = AllFiles.PMap(Queue, f => RunStack(stack.Where(s => s != null), f)).ToList();

            IEnumerable<NoMatch> noMatch = results.OfType<NoMatch>().ToList();
            Info($"No match for {noMatch.Count()} files");
            foreach (var file in noMatch)
                Info($"     {file.To}");
            if (noMatch.Any())
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

            // TODO: nexus stuff
            /*Info("Getting Nexus api_key, please click authorize if a browser window appears");
            if (IndexedArchives.Any(a => a.IniData?.General?.gameName != null))
            {
                var nexusClient = new NexusApiClient();
                if (!nexusClient.IsPremium) Error($"User {nexusClient.Username} is not a premium Nexus user, so we cannot access the necessary API calls, cannot continue");

            }
            */

            GatherArchives();

            ModList = new ModList
            {
                Archives = SelectedArchives,
                ModManager = ModManager.Vortex,
                Directives = InstallDirectives,
                GameType = Game
            };
            
            ExportModList();

            Info("Done Building ModList");
            return true;
        }

        /// <summary>
        /// Some have mods outside their game folder located
        /// </summary>
        private void AddExternalFolder()
        {
            var currentGame = GameRegistry.Games[Game];
            if (currentGame.AdditionalFolders == null || currentGame.AdditionalFolders.Count == 0) return;
            currentGame.AdditionalFolders.Do(f =>
            {
                var path = f.Replace("%documents%", KnownFolders.Documents.Path);
                if (!Directory.Exists(path)) return;
                Info($"Indexing {path}");
                VFS.AddRoot(path);
            });
        }

        private void ExportModList()
        {
            Utils.Log($"Exporting ModList to: {ModListOutputFolder}");

            // using JSON for better debugging
            ModList.ToJSON(Path.Combine(ModListOutputFolder, "modlist.json"));
            //ModList.ToCERAS(Path.Combine(ModListOutputFolder, "modlist"), ref CerasConfig.Config);

            if(File.Exists(ModListOutputFile))
                File.Delete(ModListOutputFile);

            using (var fs = new FileStream(ModListOutputFile, FileMode.Create))
            {
                using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Directory.EnumerateFiles(ModListOutputFolder, "*.*")
                        .DoProgress("Compressing ModList",
                            f =>
                            {
                                var ze = za.CreateEntry(Path.GetFileName(f));
                                using (var os = ze.Open())
                                using (var ins = File.OpenRead(f))
                                {
                                    ins.CopyTo(os);
                                }
                            });
                }
            }

            Utils.Log("Exporting ModList metadata");
            var metadata = new ModlistMetadata.DownloadMetadata
            {
                Size = File.GetSize(ModListOutputFile),
                Hash = ModListOutputFile.FileHash(),
                NumberOfArchives = ModList.Archives.Count,
                SizeOfArchives = ModList.Archives.Sum(a => a.Size),
                NumberOfInstalledFiles = ModList.Directives.Count,
                SizeOfInstalledFiles = ModList.Directives.Sum(a => a.Size)
            };
            metadata.ToJSON(ModListOutputFile + ".meta.json");

            Utils.Log("Removing ModList staging folder");
            Directory.Delete(ModListOutputFolder, true);
        }

        /*private void GenerateReport()
        {
            string css;
            using (var cssStream = Utils.GetResourceStream("Wabbajack.Lib.css-min.css"))
            using (var reader = new StreamReader(cssStream))
            {
                css = reader.ReadToEnd();
            }

            using (var fs = File.OpenWrite($"{ModList.Name}.md"))
            {
               fs.SetLength(0);
               using (var reporter = new ReportBuilder(fs, ModListOutputFolder))
               {
                   reporter.Build(this, ModList);
               }
            }
        }*/

        private void CreateMetaFiles()
        {
            Utils.Log("Getting Nexus api_key, please click authorize if a browser window appears");
            var nexusClient = new NexusApiClient();

            Directory.EnumerateFiles(DownloadsFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => File.Exists(f) && Path.GetExtension(f) != ".meta" && !File.Exists(f+".meta"))
                .Do(f =>
                {
                    Utils.Log($"Trying to create meta file for {Path.GetFileName(f)}");
                    var metaString = $"[General]\n" +
                                     $"repository=Nexus\n" +
                                     $"installed=true\n" +
                                     $"uninstalled=false\n" +
                                     $"paused=false\n" +
                                     $"removed=false\n" +
                                     $"gameName={GameName}\n";
                    string hash;
                    using(var md5 = MD5.Create())
                    using (var stream = File.OpenRead(f))
                    {
                        Utils.Log($"Calculating hash for {Path.GetFileName(f)}");
                        byte[] cH = md5.ComputeHash(stream);
                        hash = BitConverter.ToString(cH).Replace("-", "").ToLowerInvariant();
                        Utils.Log($"Hash is {hash}");
                    }

                    List<MD5Response> md5Response = nexusClient.GetModInfoFromMD5(Game, hash);
                    if (md5Response.Count >= 1)
                    {
                        var modInfo = md5Response[0].mod;
                        metaString += $"modID={modInfo.mod_id}\ndescription={NexusApiUtils.FixupSummary(modInfo.summary)}\n" +
                                      $"modName={modInfo.name}\nfileID={md5Response[0].file_details.file_id}";
                        File.WriteAllText(f+".meta",metaString, Encoding.UTF8);
                    }
                    else
                    {
                        Error("Error while getting information from nexusmods via MD5 hash!");
                    }
                    
                });
        }

        private void GatherArchives()
        {
            Info("Building a list of archives based on the files required");

            var shas = InstallDirectives.OfType<FromArchive>()
                .Select(a => a.ArchiveHashPath[0])
                .Distinct();

            var archives = IndexedArchives.OrderByDescending(f => f.File.LastModified)
                .GroupBy(f => f.File.Hash)
                .ToDictionary(f => f.Key, f => f.First());

            SelectedArchives = shas.PMap(Queue, sha => ResolveArchive(sha, archives));
        }

        private Archive ResolveArchive(string sha, IDictionary<string, IndexedArchive> archives)
        {
            if (archives.TryGetValue(sha, out var found))
            {
                if(found.IniData == null)
                    Error($"No download metadata found for {found.Name}, please use MO2 to query info or add a .meta file and try again.");

                var result = new Archive();
                result.State = (AbstractDownloadState) DownloadDispatcher.ResolveArchive(found.IniData);

                if (result.State == null)
                    Error($"{found.Name} could not be handled by any of the downloaders");

                result.Name = found.Name;
                result.Hash = found.File.Hash;
                result.Meta = found.Meta;
                result.Size = found.File.Size;

                Info($"Checking link for {found.Name}");

                if (!result.State.Verify())
                    Error(
                        $"Unable to resolve link for {found.Name}. If this is hosted on the Nexus the file may have been removed.");

                return result;
            }

            Error($"No match found for Archive sha: {sha} this shouldn't happen");
            return null;
        }

        public override Directive RunStack(IEnumerable<ICompilationStep> stack, RawSourceFile source)
        {
            Utils.Status($"Compiling {source.Path}");
            foreach (var step in stack)
            {
                var result = step.Run(source);
                if (result != null) return result;
            }

            throw new InvalidDataException("Data fell out of the compilation stack");

        }

        public override IEnumerable<ICompilationStep> GetStack()
        {
            var s = Consts.TestMode ? DownloadsFolder : VortexFolder;
            var userConfig = Path.Combine(s, "compilation_stack.yml");
            if (File.Exists(userConfig))
                return Serialization.Deserialize(File.ReadAllText(userConfig), this);

            IEnumerable<ICompilationStep> stack = MakeStack();

            File.WriteAllText(Path.Combine(s, "_current_compilation_stack.yml"),
                Serialization.Serialize(stack));

            return stack;
        }

        public override IEnumerable<ICompilationStep> MakeStack()
        {
            Utils.Log("Generating compilation stack");
            return new List<ICompilationStep>
            {
                //new IncludePropertyFiles(this),
                new IncludeVortexDeployment(this),
                new IncludeRegex(this, "^*\\.meta"),
                new IgnoreVortex(this),
                new IgnoreRegex(this, "^*__vortex_staging_folder$"),

                Game == Game.DarkestDungeon ? new IncludeRegex(this, "project\\.xml$") : null,

                new IgnoreStartsWith(this, StagingFolder),
                new IgnoreEndsWith(this, StagingFolder),

                new IgnoreGameFiles(this),

                new DirectMatch(this),

                new IgnoreGameFiles(this),

                new IgnoreWabbajackInstallCruft(this),

                new DropAll(this)
            };
        }

        public static string TypicalVortexFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex");
        }

        public static string RetrieveDownloadLocation(Game game, string vortexFolderPath = null)
        {
            vortexFolderPath = vortexFolderPath ?? TypicalVortexFolder();
            return Path.Combine(vortexFolderPath, "downloads", GameRegistry.Games[game].NexusName);
        }

        public static string RetrieveStagingLocation(Game game, string vortexFolderPath = null)
        {
            vortexFolderPath = vortexFolderPath ?? TypicalVortexFolder();
            var gameName = GameRegistry.Games[game].NexusName;
            return Path.Combine(vortexFolderPath, gameName, "mods");
        }

        public static IErrorResponse IsValidBaseDownloadsFolder(string path)
        {
            if (!Directory.Exists(path)) return ErrorResponse.Fail($"Path does not exist: {path}");
            if (Directory.EnumerateFiles(path, DownloadMarkerName, SearchOption.TopDirectoryOnly).Any()) return ErrorResponse.Success;
            return ErrorResponse.Fail($"Folder must contain {DownloadMarkerName} file");
        }

        public static IErrorResponse IsValidDownloadsFolder(string path)
        {
            return IsValidBaseDownloadsFolder(Path.GetDirectoryName(path));
        }

        public static IErrorResponse IsValidBaseStagingFolder(string path)
        {
            if (!Directory.Exists(path)) return ErrorResponse.Fail($"Path does not exist: {path}");
            if (Directory.EnumerateFiles(path, StagingMarkerName, SearchOption.TopDirectoryOnly).Any()) return ErrorResponse.Success;
            return ErrorResponse.Fail($"Folder must contain {StagingMarkerName} file");
        }

        public static IErrorResponse IsValidStagingFolder(string path)
        {
            return IsValidBaseStagingFolder(Path.GetDirectoryName(path));
        }
    }
}
