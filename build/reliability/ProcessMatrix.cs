using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetBootDhcpTool.Core;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

// Real product helper/updater processes; only the health target and HTTP server are fixtures.
// Run against an isolated source build signed by a short-lived test key.
var suite = new ProcessMatrix(Path.GetFullPath(args.Single()));
return await suite.RunAsync();

sealed partial class ProcessMatrix(string suiteRoot)
{
    readonly List<object> results = [];
    readonly RSA signer = RSA.Create();
    readonly string helper = Path.Combine(suiteRoot, "helper", "NetBootDhcpTool.SetupHelper.exe");
    readonly string updater = Path.Combine(suiteRoot, "updater", "NetBootDhcpTool.Updater.exe");
    readonly string probe = Path.Combine(suiteRoot, "probe");
    readonly string nsis = @"D:\Release\_t19_nsis_3.12_20260929\makensis.exe";
    int failures;
    readonly Dictionary<string,(byte[] Bytes,string Manifest,string Signature)> packageCache = [];
    public async Task<int> RunAsync()
    {
        Environment.SetEnvironmentVariable("NETBOOT_TEST_ROOT",suiteRoot);
        UpdateTestEnvironment.RequirePath(Path.Combine(suiteRoot,"cases"));
        if(Directory.Exists(Path.Combine(suiteRoot,"cases"))) throw new InvalidOperationException("Existing process evidence must not be overwritten.");
        signer.ImportFromPem(Environment.GetEnvironmentVariable("NETBOOT_TEST_SIGNING_PRIVATE_KEY")!);
        var cases = new (string Name, int Expected)[] {
            ("success",0), ("download-interrupted",11), ("http-404",11), ("http-500",11), ("http-503",11), ("length-mismatch",11),
            ("download-sha",12), ("signature",12), ("corrupt-7z",12),
            ("disk-download",13), ("disk-staging",13), ("disk-backup",13), ("unwritable",14),
            ("package-version",12), ("manifest-package",12), ("payload-sha",12),
            ("traversal",12), ("absolute",12), ("unc",12), ("ads",12), ("duplicate",12), ("case-collision",12),
            ("reparse-file",12), ("symlink-root",12), ("junction-root",12), ("staging-replacement",12), ("locked-stream",0),
            ("file-lock",21), ("replace-failure",21), ("backup-failure",10), ("journal-failure",10),
            ("kill-backup",24), ("kill-replace",24), ("updater-abnormal",24), ("helper-kill",-1),
            ("backup-corrupt",22), ("app-not-executable",23), ("app-immediate-exit",23),
            ("health-timeout",23), ("health-version",23), ("health-inventory",23),
            ("nsis-success",0), ("nsis-file-lock",21), ("nsis-health-version",23), ("nsis-backup-corrupt",22), ("nsis-helper-kill",-1)
        };
        foreach (var item in cases) await RunCaseAsync(item.Name, item.Expected);
        await RunRealLifecycleAsync();
        signer.Dispose();
        File.WriteAllText(Path.Combine(suiteRoot,"matrix-summary.json"),JsonSerializer.Serialize(new { Timestamp=DateTimeOffset.UtcNow, Passed=results.Count-failures, Failed=failures, Cases=results },JsonStore.Options));
        Console.WriteLine($"PROCESS_MATRIX finished cases={results.Count} failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    async Task RunCaseAsync(string name, int expected)
    {
        var clock=Stopwatch.StartNew();
        var root=Path.Combine(suiteRoot,"cases",name);
        var install=Path.Combine(root,"install");
        var data=Path.Combine(root,"user-data");
        Directory.CreateDirectory(install); Directory.CreateDirectory(data); Directory.CreateDirectory(Path.Combine(root,"temp"));
        File.WriteAllText(Path.Combine(root,UpdateTestEnvironment.MarkerName),"NetBootDhcpTool isolated integration test v1");
        var mode=name.Replace("nsis-","");
        var environment=new Dictionary<string,string> {
            ["NETBOOT_TEST_ROOT"]=root, ["NETBOOT_INTEGRATION_TEST"]="1", ["NETBOOT_DATA_DIRECTORY"]=data,
            ["NETBOOT_TEST_INSTALL_ROOT"]=install, ["NETBOOT_TEST_OFFLINE"]="1", ["NETBOOT_TEST_HEALTH_TIMEOUT"]="short",
            ["TEMP"]=Path.Combine(root,"temp"), ["TMP"]=Path.Combine(root,"temp")
        };
        Process? process=null; Process? recovery=null; FileStream? locked=null; FaultServer? server=null;
        string? requestFile=null; bool attackDenied=false; bool recoveryPassed=false;
        int? actual=null;
        try
        {
            var old=ProbeFiles("1.0.20");
            foreach(var item in old) File.WriteAllBytes(Path.Combine(install,item.Key),item.Value);
            File.WriteAllBytes(Path.Combine(install,UpdatePackageApplier.InstallManifestName),InstallManifest("1.0.20",old));
            var baseline=Snapshot(install);
            File.WriteAllText(Path.Combine(data,"preserved-config.json"),"{\"network\":\"retain\",\"user\":\"retain\"}");
            var preserved=Hash(File.ReadAllBytes(Path.Combine(data,"preserved-config.json")));
            var target=ProbeFiles("1.1.0");
            if(mode=="app-not-executable") target["NetBootDhcpTool.exe"]=[1,2,3,4];
            var package=BuildPackage(root,target,mode);
            File.WriteAllText(Path.Combine(root,"manifest.json"),package.Manifest);
            File.WriteAllText(Path.Combine(root,"manifest.sig"),mode=="signature" ? Convert.ToBase64String(new byte[384]) : package.Signature);
            if(mode=="manifest-package") { var bytes=File.ReadAllBytes(package.Path); bytes[^1]^=1; File.WriteAllBytes(package.Path,bytes); }
            if(mode.StartsWith("disk-")) environment["NETBOOT_TEST_FAULT"]="disk:"+mode[5..];
            if(mode=="file-lock") locked=File.Open(Path.Combine(install,"probe-version.txt"),FileMode.Open,FileAccess.Read,FileShare.Read);
            if(mode=="replace-failure") environment["NETBOOT_TEST_FAULT"]="fail:replace-1";
            if(mode=="backup-failure") environment["NETBOOT_TEST_FAULT"]="fail:backup-file";
            if(mode=="journal-failure") environment["NETBOOT_TEST_FAULT"]="fail:journal-write-applying";
            if(mode=="kill-backup") environment["NETBOOT_TEST_FAULT"]="pause:backup-file";
            if(mode=="kill-replace") environment["NETBOOT_TEST_FAULT"]="pause:replace-1";
            if(mode=="updater-abnormal") environment["NETBOOT_TEST_FAULT"]="exit:replace-1";
            if(mode=="helper-kill") environment["NETBOOT_TEST_FAULT"]="pause:applying";
            if(mode=="backup-corrupt") environment["NETBOOT_TEST_FAULT"]="pause:applying";
            if(mode=="staging-replacement") environment["NETBOOT_TEST_FAULT"]="pause:staging-created";
            if(mode=="locked-stream") environment["NETBOOT_TEST_FAULT"]="pause:package-verified";
            if(mode=="health-inventory") environment["NETBOOT_TEST_FAULT"]="pause:health-check";
            if(mode.StartsWith("app-") && mode!="app-not-executable") environment["NETBOOT_TEST_HEALTH_MODE"]="immediate-exit";
            if(mode=="health-timeout") environment["NETBOOT_TEST_HEALTH_MODE"]="timeout";
            if(mode=="health-version") environment["NETBOOT_TEST_HEALTH_MODE"]="version";
            if(mode=="unwritable")
            {
                // Windows administrators can bypass many ACLs; deny FILE_ADD_FILE for this exact test directory.
                var acl = new DirectoryInfo(install).GetAccessControl();
                var identity=System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.CreateFiles,System.Security.AccessControl.AccessControlType.Deny));
                new DirectoryInfo(install).SetAccessControl(acl);
            }
            if(mode=="reparse-file")
            {
                var path=Path.Combine(install,"probe-version.txt"); File.Move(path,Path.Combine(root,"linked-original.txt"));
                File.CreateSymbolicLink(path,Path.Combine(root,"linked-original.txt"));
            }
            if(mode is "junction-root" or "symlink-root")
            {
                Directory.Move(install,Path.Combine(root,"original-install"));
                if(mode=="symlink-root") Directory.CreateSymbolicLink(install,Path.Combine(root,"original-install"));
                else
                {
                    using var junction=Start("cmd.exe",["/c","mklink","/J",install,Path.Combine(root,"original-install")],environment);
                    await junction.WaitForExitAsync();if(junction.ExitCode!=0)throw new Exception("Test junction creation failed.");
                }
            }
            var source=package.Path;
            if(mode.StartsWith("download-") || mode.StartsWith("http-") || mode=="length-mismatch")
            {
                server=new FaultServer(mode,File.ReadAllBytes(package.Path));
                environment["NETBOOT_TEST_HTTP_ENDPOINT"]=server.Endpoint;
                environment["NETBOOT_TEST_OFFLINE"]="0";
                source="";
            }
            if(name.StartsWith("nsis-"))
            {
                var installer=await BuildNsisAsync(root,package);
                process=Start(installer,["/S","/D="+install],environment);
            }
            else process=Start(helper,["--full-install",source,Path.Combine(root,"manifest.json"),Path.Combine(root,"manifest.sig"),install,"--silent"],environment);
            var checkpoint=environment.GetValueOrDefault("NETBOOT_TEST_FAULT");
            if(checkpoint?.StartsWith("pause:")==true)
            {
                var phase=checkpoint[6..]; var ready=Path.Combine(root,"fault-"+phase+".ready");
                await WaitAsync(()=>File.Exists(ready)||process.HasExited,TimeSpan.FromSeconds(60));
                if(!File.Exists(ready)) throw new Exception("Process exited without reaching expected checkpoint.");
                var affected=File.ReadAllText(ready);
                if(mode=="staging-replacement")
                {
                    Directory.Move(affected,affected+".original");
                    var redirected=Path.Combine(root,"redirected"); Directory.CreateDirectory(redirected);
                    Directory.CreateSymbolicLink(affected,redirected);
                }
                else if(mode=="locked-stream")
                {
                    try { File.Move(affected,affected+".attacker",overwrite:true); }
                    catch(IOException) { attackDenied=true; }
                    if(!attackDenied) throw new Exception("Package replacement was not blocked by the locked handle.");
                }
                else if(mode=="health-inventory") File.WriteAllText(Path.Combine(install,"probe-version.txt"),"tampered-after-launch");
                else if(mode=="backup-corrupt")
                {
                    File.WriteAllText(Path.Combine(affected,"probe-version.txt"),"corrupt-backup");
                    // Stop the new application before health; rollback must reject the corrupted backup.
                    environment["NETBOOT_TEST_HEALTH_MODE"]="version";
                    File.WriteAllText(Path.Combine(install,"probe-version.txt"),"locked-test-trigger");
                    locked=File.Open(Path.Combine(install,"probe-version.txt"),FileMode.Open,FileAccess.Read,FileShare.Read);
                }
                else if(mode is "kill-backup" or "kill-replace" or "updater-abnormal" or "helper-kill")
                {
                    requestFile=Directory.GetFiles(Path.Combine(data,"updates","requests"),"*.json").Single();
                    if(mode=="helper-kill") process.Kill(entireProcessTree:true);
                    else
                    {
                        var active=Process.GetProcessesByName("NetBootDhcpTool.Updater").Single(candidate=> {
                            try { return candidate.MainModule?.FileName?.StartsWith(data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)==true; }
                            catch { return false; }
                        });
                        active.Kill(); await active.WaitForExitAsync(); active.Dispose();
                    }
                }
                File.WriteAllText(ready+".continue","");
            }
            await WaitAsync(()=>process.HasExited,TimeSpan.FromSeconds(120));
            actual=process.ExitCode;
            locked?.Dispose(); locked=null;
            if(mode=="unwritable") RestoreAcl(install);
            if(mode is "kill-backup" or "kill-replace" or "updater-abnormal" or "helper-kill")
            {
                requestFile ??= Directory.GetFiles(Path.Combine(data,"updates","requests"),"*.json").Single();
                environment.Remove("NETBOOT_TEST_FAULT");
                recovery=Start(updater,["--recover",requestFile!],environment);
                await WaitAsync(()=>recovery.HasExited,TimeSpan.FromSeconds(30));
                if(recovery.ExitCode!=21) throw new Exception("Recovery updater exit="+recovery.ExitCode);
                recoveryPassed=true;
            }
            if(actual!=expected) throw new Exception($"Expected exit {expected}, actual {actual}.");
            var final=Snapshot(install);
            if(expected!=0 && expected!=22 && mode!="backup-corrupt")
            {
                foreach(var file in baseline) if(!final.TryGetValue(file.Key,out var hash)||file.Value!=hash) throw new Exception("Old installation changed: "+file.Key);
                if(final.Keys.Any(file=>!baseline.ContainsKey(file)&&file!="previous-version-restarted.test")) throw new Exception("Unexpected managed file outside transaction directories.");
            }
            if(expected==0) await UpdatePackageApplier.VerifyInstalledFilesAsync(install,"1.1.0");
            if(Hash(File.ReadAllBytes(Path.Combine(data,"preserved-config.json")))!=preserved) throw new Exception("User data was changed.");
            if(expected is 21 or 23 && !File.Exists(Path.Combine(install,"previous-version-restarted.test")))
                await WaitAsync(()=>File.Exists(Path.Combine(install,"previous-version-restarted.test")),TimeSpan.FromSeconds(5));
            var result=new { Name=name,Passed=true,ExpectedExit=expected,ExitCode=actual,RecoveryPassed=recoveryPassed,AttackDenied=attackDenied,ElapsedSeconds=clock.Elapsed.TotalSeconds,ServerRequests=server?.Requests??0,Installation=install };
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch(Exception ex)
        {
            failures++;
            var result=new { Name=name,Passed=false,ExpectedExit=expected,ExitCode=actual,RecoveryPassed=recoveryPassed,ElapsedSeconds=clock.Elapsed.TotalSeconds,Error=ex.ToString(),Installation=install };
            results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        finally
        {
            locked?.Dispose(); server?.Dispose();
            if(mode=="unwritable") RestoreAcl(install);
            if(process is {HasExited:false}) { process.Kill(entireProcessTree:true); await process.WaitForExitAsync(); }
            process?.Dispose(); recovery?.Dispose();
            File.WriteAllText(Path.Combine(suiteRoot,"matrix-progress.json"),JsonSerializer.Serialize(results,JsonStore.Options));
        }
    }

    Dictionary<string,byte[]> ProbeFiles(string version)
    {
        var files=new Dictionary<string,byte[]> { ["probe-version.txt"]=Encoding.UTF8.GetBytes(version) };
        foreach(var path in Directory.GetFiles(probe).Where(path=>!path.EndsWith(".pdb"))) files[Path.GetFileName(path)]=File.ReadAllBytes(path);
        files["NetBootDhcpTool.Updater.exe"]=File.ReadAllBytes(updater);
        return files;
    }
    static byte[] InstallManifest(string version,Dictionary<string,byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new UpdateInstallManifest {
        ProductId=UpdatePackageApplier.ProductId,Version=version,
        Files=files.Select(item=>new UpdateFileEntry {Path=item.Key,Size=item.Value.Length,Sha256=Hash(item.Value)}).ToList()
    },JsonStore.Options);

    (string Path,string Manifest,string Signature) BuildPackage(string root,Dictionary<string,byte[]> files,string mode)
    {
        var cacheKey = mode is "package-version" or "payload-sha" or "app-not-executable" or "corrupt-7z"
            or "traversal" or "absolute" or "unc" or "ads" or "duplicate" or "case-collision" ? mode : "normal";
        var path=Path.Combine(root,"NetBootDhcpTool-full-v1.1.0.7z");
        if (packageCache.TryGetValue(cacheKey,out var cached))
        {
            File.WriteAllBytes(path,cached.Bytes);
            return (path,cached.Manifest,cached.Signature);
        }
        files[UpdatePackageApplier.InstallManifestName]=InstallManifest("1.1.0",files);
        var document=new UpdatePackageDocument { Kind="Full",TargetVersion=mode=="package-version" ? "1.1.9":"1.1.0",
            Files=files.Select(item=>new UpdatePackageFile {Path=item.Key,Size=item.Value.Length,Sha256=Hash(item.Value)}).ToList() };
        if(mode=="payload-sha") document.Files[0].Sha256=new string('f',64);
        if(mode=="corrupt-7z") File.WriteAllBytes(path,[0x37,0x7a,0xbc,0xaf,0x27,0x1c,0xff]);
        else
        {
            using var stream=File.Create(path);
            using var writer=SevenZipWriter.OpenWriter(stream,new SevenZipWriterOptions(CompressionType.LZMA2) {CompressionLevel=1});
            using(var input=new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document,JsonStore.Options))) writer.Write(UpdatePackageApplier.PackageDocumentName,input,null);
            foreach(var item in files) {using var input=new MemoryStream(item.Value);writer.Write("payload/"+item.Key,input,null);}
            var attack=mode switch { "traversal"=>"payload/../escape.txt", "absolute"=>"/payload/escape.txt", "unc"=>"\\\\server\\share\\escape", "ads"=>"payload/file.txt:ads", "duplicate"=>"payload/probe-version.txt", "case-collision"=>"payload/PROBE-VERSION.TXT", _=>null };
            if(attack is not null) { using var input=new MemoryStream([1,2,3]);writer.Write(attack,input,null); }
        }
        var metadata=new UpdatePackageMetadata {Kind="Full",FileName=Path.GetFileName(path),Size=new FileInfo(path).Length,Sha256=Hash(File.ReadAllBytes(path)),DownloadUrl="https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.1.0/"+Path.GetFileName(path)};
        var manifest=JsonSerializer.Serialize(new UpdateManifest {Version="1.1.0",ArchiveName="NetBootDhcpTool-v1.1.0.7z",ArchiveSha256=metadata.Sha256,
            DownloadUrl="https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.1.0/NetBootDhcpTool-v1.1.0.7z",ReleasePageUrl="https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.1.0",SevenZipPackages=[metadata]},JsonStore.Options);
        var signature = Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(manifest),HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        packageCache[cacheKey]=(File.ReadAllBytes(path),manifest,signature);
        return (path,manifest,signature);
    }
    async Task<string> BuildNsisAsync(string root,(string Path,string Manifest,string Signature) package)
    {
        var versionSource = System.Xml.Linq.XDocument.Load(Path.Combine(suiteRoot,"source","build","Version.props")).Root!.Element("PropertyGroup")!;
        var args=new [] {"-DPRODUCT_VERSION=1.1.0", "-DPE_VERSION=1.1.0.0", "-DPRODUCT_NAME="+versionSource.Element("NetBootProductName")!.Value, "-DCOMPANY_NAME="+versionSource.Element("NetBootCompanyName")!.Value, "-DPRODUCT_COPYRIGHT="+versionSource.Element("NetBootCopyright")!.Value,"-DSETUP_HELPER="+helper,"-DMANIFEST="+Path.Combine(root,"manifest.json"),"-DSIGNATURE="+Path.Combine(root,"manifest.sig"),"-DFULL_PACKAGE_NAME="+Path.GetFileName(package.Path),"-DOUTPUT_DIRECTORY="+root,"-DUSER_CANCELLED_EXIT_CODE="+(int)InstallExitCode.Cancelled,Path.Combine(suiteRoot,"source","installer","NetBootDhcpTool.Setup.nsi")};
        using var compiler=Start(nsis,args,new Dictionary<string,string>());
        await compiler.WaitForExitAsync();
        if(compiler.ExitCode!=0) throw new Exception("NSIS test fixture compilation failed.");
        return Path.Combine(root,"NetBootDhcpTool-Setup-v1.1.0.exe");
    }
    static Process Start(string exe,IEnumerable<string> args,Dictionary<string,string> environment)
    {
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)!};
        start.Environment.Remove("NETBOOT_TEST_SIGNING_PRIVATE_KEY");
        foreach(var item in environment) start.Environment[item.Key]=item.Value;
        foreach(var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)??throw new Exception("Cannot start test process: "+exe);
    }
    static async Task WaitAsync(Func<bool> condition,TimeSpan timeout)
    {
        var watch=Stopwatch.StartNew(); while(!condition()) {if(watch.Elapsed>timeout)throw new TimeoutException("Test process/checkpoint timed out.");await Task.Delay(50);}
    }
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static Dictionary<string,string> Snapshot(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .Where(path=>!Path.GetRelativePath(root,path).StartsWith("updates"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
        .ToDictionary(path=>Path.GetRelativePath(root,path),path=>Hash(File.ReadAllBytes(path)),StringComparer.OrdinalIgnoreCase);
    static void RestoreAcl(string root)
    {
        if(!Directory.Exists(root))return;
        var info=new DirectoryInfo(root);var acl=info.GetAccessControl();
        foreach(System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)))
            if(rule.AccessControlType==System.Security.AccessControl.AccessControlType.Deny)acl.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(acl);
    }
}

sealed class FaultServer : IDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0); readonly CancellationTokenSource stop=new();
    public string Endpoint {get;} public int Requests;
    public FaultServer(string mode,byte[] bytes)
    {
        listener.Start();Endpoint="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/";
        _=Task.Run(async()=> {
            while(!stop.IsCancellationRequested)
            {
                TcpClient client; try {client=await listener.AcceptTcpClientAsync(stop.Token);} catch {break;}
                using(client)
                {
                    Requests++;var stream=client.GetStream();var request=new byte[4096]; var received=await stream.ReadAsync(request,stop.Token);
                    if(received==0)continue;
                    var status=mode=="http-404"?404:mode=="http-500"?500:mode=="http-503"?503:200;
                    var payload=mode=="download-sha"?new byte[bytes.Length]:bytes;
                    var length=mode=="length-mismatch"?payload.Length+100:payload.Length;
                    var headers=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers,stop.Token);
                    if(status==200)try{await stream.WriteAsync(mode=="download-interrupted" ? payload.AsMemory(0,Math.Min(4096,payload.Length)) : payload,stop.Token);}catch(IOException){}
                }
            }
        });
    }
    public void Dispose(){stop.Cancel();listener.Stop();stop.Dispose();}
}
