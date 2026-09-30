using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetBootDhcpTool.Core;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

sealed partial class ProcessMatrix
{
    async Task RunRealLifecycleAsync()
    {
        var clock=Stopwatch.StartNew(); var root=Path.Combine(suiteRoot,"real-lifecycle");
        var install=Path.Combine(root,"install"); var data=Path.Combine(root,"user-data");
        Directory.CreateDirectory(root);Directory.CreateDirectory(data);Directory.CreateDirectory(Path.Combine(root,"temp"));
        File.WriteAllText(Path.Combine(root,UpdateTestEnvironment.MarkerName),"NetBootDhcpTool isolated integration test v1");
        var env=new Dictionary<string,string> { ["NETBOOT_TEST_ROOT"]=root,["NETBOOT_INTEGRATION_TEST"]="1",
            ["NETBOOT_DATA_DIRECTORY"]=data,["NETBOOT_TEST_INSTALL_ROOT"]=install,["NETBOOT_TEST_OFFLINE"]="1",
            ["TEMP"]=Path.Combine(root,"temp"),["TMP"]=Path.Combine(root,"temp") };
        Process? setup=null;Process? old=null;Process? app=null;RealReleaseServer? server=null;
        try
        {
            const string official=@"D:\Release\v1.0.20\NetBootDhcpTool-full-v1.0.20.zip";
            if(Hash(File.ReadAllBytes(official))!="f925e90a321c9a946f7d18a65d3ec3283f8099df55e81d0fa5f141cc33d90cde")throw new Exception("Official v1.0.20 fixture SHA changed.");
            var unpack=Path.Combine(root,"official-unpack");ZipFile.ExtractToDirectory(official,unpack);
            var originalManifest=Directory.GetFiles(unpack,UpdatePackageApplier.InstallManifestName,SearchOption.AllDirectories).Single();
            Directory.Move(Path.GetDirectoryName(originalManifest)!,install);
            var fixture=@"D:\Release\_t19_setup_process_test2_20260930\isolated-user-data";
            foreach(var path in Directory.GetFiles(fixture,"*",SearchOption.AllDirectories))
            {
                var relative=Path.GetRelativePath(fixture,path);
                if(relative.StartsWith("updates"+Path.DirectorySeparatorChar)||relative.StartsWith("Updater"+Path.DirectorySeparatorChar)||relative.StartsWith("logs"+Path.DirectorySeparatorChar)||relative.EndsWith(".bak"))continue;
                var destination=Path.Combine(data,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination);
            }
            File.WriteAllText(Path.Combine(data,"custom-state.test"),"T19 data must survive both installations");
            var settingsBefore=JsonNode.Parse(File.ReadAllText(Path.Combine(data,"config","appsettings.json")))!.AsObject();
            var bytePreserved=Directory.GetFiles(data,"*",SearchOption.AllDirectories)
                .Where(path=>!path.EndsWith("appsettings.json")&&!path.EndsWith("favorites.json"))
                .ToDictionary(path=>Path.GetRelativePath(data,path),path=>Hash(File.ReadAllBytes(path)));
            var favoritesBefore=JsonNode.Parse(File.ReadAllText(Path.Combine(data,"config","favorites.json")))!.AsArray();
            old=Start(Path.Combine(install,"NetBootDhcpTool.exe"),[],env);
            await WaitAsync(()=> { old.Refresh(); return old.HasExited||old.MainWindowHandle!=IntPtr.Zero; },TimeSpan.FromSeconds(45));
            if(old.HasExited)throw new Exception("Official v1.0.20 did not start normally.");
            var first=BuildRealPackage(root,"1.1.0");
            File.WriteAllText(Path.Combine(root,"manifest.json"),first.Manifest);File.WriteAllText(Path.Combine(root,"manifest.sig"),first.Signature);
            var installer=await BuildNsisAsync(root,first);
            setup=Start(installer,["/S","/D="+install],env);
            await WaitAsync(()=>setup.HasExited,TimeSpan.FromMinutes(4));
            if(setup.ExitCode!=0)throw new Exception("Real Setup final exit="+setup.ExitCode);
            if(!old.HasExited)throw new Exception("Old application was not closed.");
            await UpdatePackageApplier.VerifyInstalledFilesAsync(install,"1.1.0");
            await CloseOwnedAppsAsync(install);
            CheckData(data,settingsBefore,bytePreserved,favoritesBefore);
            var firstResult=new {Name="real-v1.0.20-full-install",Passed=true,SetupExit=setup.ExitCode,
                Version="1.1.0",SetupSize=new FileInfo(installer).Length,Full7zSize=new FileInfo(first.Path).Length,ElapsedSeconds=clock.Elapsed.TotalSeconds};
            results.Add(firstResult);Console.WriteLine(JsonSerializer.Serialize(firstResult));

            var secondRoot=Path.Combine(root,"n-plus-one");Directory.CreateDirectory(secondRoot);
            var second=BuildRealPackage(secondRoot,"1.1.1");
            server=new RealReleaseServer(second.Manifest,second.Signature,File.ReadAllBytes(second.Path));
            env["NETBOOT_TEST_HTTP_ENDPOINT"]=server.Endpoint;env["NETBOOT_TEST_AUTOMATIC_UPDATE"]="1";
            app=Start(Path.Combine(install,"NetBootDhcpTool.exe"),[],env);
            var driver=Path.Combine(data,"integration-auto-update.json");
            await WaitAsync(()=>File.Exists(driver)||app.HasExited,TimeSpan.FromMinutes(3));
            if(!File.Exists(driver))throw new Exception("Real App exited before its update driver report.");
            var driven=JsonNode.Parse(File.ReadAllText(driver))!;
            if(driven["Phase"]?.ToString()!="accepted")throw new Exception("Real App automatic update failed: "+driven.ToJsonString());
            await WaitAsync(()=>app.HasExited,TimeSpan.FromMinutes(1));
            var status=Path.Combine(data,"updates","update-status.json");
            await WaitAsync(()=>File.Exists(status)&&JsonNode.Parse(File.ReadAllText(status)) is { } state
                && state["TargetVersion"]?.ToString()=="1.1.1" && state["IsTerminal"]?.GetValue<bool>()==true,TimeSpan.FromMinutes(3));
            var final=JsonSerializer.Deserialize<UpdateTransactionStatus>(File.ReadAllText(status),JsonStore.Options)!;
            if(final.ExitCode!=InstallExitCode.Success||final.TargetVersion!="1.1.1")throw new Exception("N+1 transaction did not become healthy: "+File.ReadAllText(status));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(install,"1.1.1");
            await CloseOwnedAppsAsync(install);
            CheckData(data,settingsBefore,bytePreserved,favoritesBefore);
            if(!server.Paths.Any(path=>path.Contains("/api/v5/"))||!server.Paths.Any(path=>path.Contains("/shashouaq/")))throw new Exception("Both source discovery paths were not exercised.");
            var secondResult=new {Name="real-app-full7z-n-plus-one",Passed=true,Version="1.1.1",Full7zSize=new FileInfo(second.Path).Length,
                Sha256=Hash(File.ReadAllBytes(second.Path)),Result=final,Driver=driven,Requests=server.Paths.ToArray(),DataPreservation=true,ElapsedSeconds=clock.Elapsed.TotalSeconds};
            results.Add(secondResult);Console.WriteLine(JsonSerializer.Serialize(secondResult));
            File.WriteAllText(Path.Combine(root,"lifecycle-result.json"),JsonSerializer.Serialize(new {First=firstResult,Second=secondResult},JsonStore.Options));
        }
        catch(Exception ex)
        {
            failures++;var failed=new {Name="real-two-stage-lifecycle",Passed=false,Error=ex.ToString(),ElapsedSeconds=clock.Elapsed.TotalSeconds};
            results.Add(failed);Console.WriteLine(JsonSerializer.Serialize(failed));
        }
        finally
        {
            server?.Dispose();await CloseOwnedAppsAsync(install);
            if(setup is {HasExited:false})setup.Kill(entireProcessTree:true);
            setup?.Dispose();old?.Dispose();app?.Dispose();
        }
    }

    (string Path,string Manifest,string Signature) BuildRealPackage(string root,string version)
    {
        var product=Path.Combine(suiteRoot,"real-product-"+version);
        var files=Directory.GetFiles(product,"*",SearchOption.AllDirectories)
            .Where(path=>!path.EndsWith(".pdb"))
            .ToDictionary(path=>Path.GetRelativePath(product,path).Replace('\\','/'),File.ReadAllBytes);
        files[UpdatePackageApplier.InstallManifestName]=InstallManifest(version,files);
        var document=new UpdatePackageDocument {Kind="Full",TargetVersion=version,Files=files.Select(item=>new UpdatePackageFile {Path=item.Key,Size=item.Value.Length,Sha256=Hash(item.Value)}).ToList()};
        var path=Path.Combine(root,$"NetBootDhcpTool-full-v{version}.7z");
        var packageRoot = Path.Combine(root,"package-source");Directory.CreateDirectory(packageRoot);
        File.WriteAllBytes(Path.Combine(packageRoot,UpdatePackageApplier.PackageDocumentName),JsonSerializer.SerializeToUtf8Bytes(document,JsonStore.Options));
        foreach(var item in files)
        {
            var destination=Path.Combine(packageRoot,"payload",item.Key.Replace('/',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,item.Value);
        }
        var start=new ProcessStartInfo(@"C:\Program Files\7-Zip\7z.exe") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=packageRoot};
        start.Environment.Remove("NETBOOT_TEST_SIGNING_PRIVATE_KEY");
        foreach(var argument in new[]{"a","-t7z",path,"update-package.json","payload","-mx=9","-m0=lzma2","-ms=on","-mmt=on"})start.ArgumentList.Add(argument);
        using(var compress=Process.Start(start)!) {compress.WaitForExit();if(compress.ExitCode!=0)throw new Exception("Formal-format 7z test package creation failed.");}
        var metadata=new UpdatePackageMetadata {Kind="Full",FileName=Path.GetFileName(path),Size=new FileInfo(path).Length,Sha256=Hash(File.ReadAllBytes(path)),
            DownloadUrl=$"https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v{version}/"+Path.GetFileName(path),
            DownloadMirrors=[$"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{version}/"+Path.GetFileName(path)]};
        var options=new JsonSerializerOptions(JsonStore.Options) {PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
        var manifest=JsonSerializer.Serialize(new UpdateManifest {Version=version,ArchiveName=$"NetBootDhcpTool-v{version}.7z",ArchiveSha256=metadata.Sha256,
            DownloadUrl=$"https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v{version}/NetBootDhcpTool-v{version}.7z",
            DownloadMirrors=[$"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{version}/NetBootDhcpTool-v{version}.7z"],
            ReleasePageUrl=$"https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v{version}",SevenZipPackages=[metadata]},options);
        return (path,manifest,Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(manifest),HashAlgorithmName.SHA256,RSASignaturePadding.Pss)));
    }

    static void CheckData(string data,JsonObject settingsBefore,Dictionary<string,string> preserved,JsonArray favoritesBefore)
    {
        var after=JsonNode.Parse(File.ReadAllText(Path.Combine(data,"config","appsettings.json")))!.AsObject();
        var allowed=new HashSet<string> {"WindowLeft","WindowTop","WindowWidth","WindowHeight","WindowState","LastTab","LogPanelExpanded"};
        foreach(var item in settingsBefore)if(!allowed.Contains(item.Key)&&!JsonNode.DeepEquals(item.Value,after[item.Key]))throw new Exception("Stable setting was lost: "+item.Key);
        foreach(var item in preserved)if(Hash(File.ReadAllBytes(Path.Combine(data,item.Key)))!=item.Value)throw new Exception("Preserved user/network data changed: "+item.Key);
        var favoritesAfter=JsonNode.Parse(File.ReadAllText(Path.Combine(data,"config","favorites.json")))!.AsArray();
        foreach(var favorite in favoritesBefore)if(!favoritesAfter.Any(item=>JsonNode.DeepEquals(item,favorite)))throw new Exception("An existing favorite was lost.");
    }

    static async Task CloseOwnedAppsAsync(string install)
    {
        foreach(var process in Process.GetProcessesByName("NetBootDhcpTool"))
        {
            using(process)
            {
                string? path;try{path=process.MainModule?.FileName;}catch{continue;}
                if(path?.Equals(Path.Combine(install,"NetBootDhcpTool.exe"),StringComparison.OrdinalIgnoreCase)!=true)continue;
                process.CloseMainWindow();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill();await process.WaitForExitAsync();throw new Exception("Test App did not close normally.");}
            }
        }
    }
}

sealed class RealReleaseServer : IDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();
    public string Endpoint {get;} public System.Collections.Concurrent.ConcurrentBag<string> Paths=[];
    public RealReleaseServer(string manifest,string signature,byte[] archive)
    {
        listener.Start();Endpoint="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/";
        _=Task.Run(async()=> {
            while(!stop.IsCancellationRequested)
            {
                TcpClient client;try{client=await listener.AcceptTcpClientAsync(stop.Token);}catch{break;}
                _=Task.Run(async()=> {
                    using(client)try {
                        var stream=client.GetStream();var buffer=new byte[8192];var count=await stream.ReadAsync(buffer,stop.Token);
                        if(count==0)return;var first=Encoding.ASCII.GetString(buffer,0,count).Split('\r')[0];var path=first.Split(' ')[1];Paths.Add(path);
                        byte[] content;
                        if(path.Contains("/api/v5/")&&path.EndsWith("/latest"))content=Encoding.UTF8.GetBytes("{\"id\":1,\"tag_name\":\"v1.1.1\",\"prerelease\":false}");
                        else if(path.Contains("/attach_files"))content=Encoding.UTF8.GetBytes("[{\"name\":\"latest-v2.json\",\"browser_download_url\":\"https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.1.1/latest-v2.json\"},{\"name\":\"latest-v2.json.sig\",\"browser_download_url\":\"https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.1.1/latest-v2.json.sig\"},{\"name\":\"v1.1.1.zip\",\"browser_download_url\":\"https://gitee.com/source-code.zip\"}]");
                        else if(path.EndsWith("latest-v2.json.sig"))content=Encoding.UTF8.GetBytes(signature);
                        else if(path.EndsWith("latest-v2.json"))content=Encoding.UTF8.GetBytes(manifest);
                        else content=archive;
                        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header,stop.Token);await stream.WriteAsync(content,stop.Token);
                    }catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                });
            }
        });
    }
    public void Dispose(){stop.Cancel();listener.Stop();}
}
