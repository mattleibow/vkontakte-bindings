#tool nuget:https://nuget.org/api/v2/?package=XamarinComponent

#addin nuget:https://nuget.org/api/v2/?package=Octokit&version=0.20.0
#addin nuget:https://nuget.org/api/v2/?package=Cake.Xamarin
#addin nuget:https://nuget.org/api/v2/?package=Cake.XCode
#addin nuget:https://nuget.org/api/v2/?package=Cake.FileHelpers

using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Octokit;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default").ToUpper();

// special logic to tweak builds for platform limitations

var ForMacOnly = target.EndsWith("-MAC");
var ForWindowsOnly = target.EndsWith("-WINDOWS");
var ForEverywhere = !ForMacOnly && !ForWindowsOnly;

var ForWindows = ForEverywhere || !ForMacOnly;
var ForMac = ForEverywhere || !ForWindowsOnly;

target = target.Replace("-MAC", string.Empty).Replace("-WINDOWS", string.Empty);

Information("Building target '{0}' for {1}.", target, ForEverywhere ? "everywhere" : (ForWindowsOnly ? "Windows only" : "Mac only"));

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

// the tools
FilePath XamarinComponentPath = "./tools/XamarinComponent/tools/xamarin-component.exe";

// the output folder
DirectoryPath outDir = "./output/";
FilePath outputZip = "output.zip";
if (!DirectoryExists(outDir)) {
    CreateDirectory(outDir);
}

// get CI variables
var sha = EnvironmentVariable("APPVEYOR_REPO_COMMIT") ?? EnvironmentVariable("TRAVIS_COMMIT");
var branch = EnvironmentVariable("APPVEYOR_REPO_BRANCH") ?? EnvironmentVariable("TRAVIS_BRANCH");
var tag = EnvironmentVariable("APPVEYOR_REPO_TAG_NAME") ?? EnvironmentVariable("TRAVIS_TAG");
var pull = EnvironmentVariable("APPVEYOR_PULL_REQUEST_NUMBER") ?? EnvironmentVariable("TRAVIS_PULL_REQUEST");

// get the temporary build artifacts filename
var buildType = "COMMIT";
if (!string.IsNullOrEmpty(pull) && !string.Equals(pull, "false", StringComparison.OrdinalIgnoreCase)) {
    buildType = "PULL" + pull;
} else if (!string.IsNullOrEmpty(tag)) {
    buildType = "TAG";
}
var tagOrBranch = branch;
if (!string.IsNullOrEmpty(tag)) {
    tagOrBranch = tag;
}
var TemporaryArtifactsFilename = string.Format("{0}_{1}_{2}.zip", buildType, tagOrBranch, sha);

// the GitHub communication (for storing the temporary build artifacts)
var GitHubToken = EnvironmentVariable("GitHubToken");
var GitHubUser = "mattleibow";
var GitHubRepository = "vkontakte-bindings";
var GitHubBuildTag = "CI";

public enum TargetOS {
    Windows,
    Mac,
    Android,
    iOS,
    tvOS,
}

//////////////////////////////////////////////////////////////////////
// VERSIONS
//////////////////////////////////////////////////////////////////////

const string vk_ios_version      = "1.3.16";
const string vk_android_version  = "1.6.7";

////////////////////////////////////////////////////////////////////////////////////////////////////
// TOOLS & FUNCTIONS - the bits to make it all work
////////////////////////////////////////////////////////////////////////////////////////////////////

var NugetToolPath = File ("./tools/nuget.exe");
var XamarinComponentToolPath = File ("./tools/XamarinComponent/tools/xamarin-component.exe");
var GitToolPath = EnvironmentVariable ("GIT_EXE") ?? (IsRunningOnWindows () ? "C:\\Program Files (x86)\\Git\\bin\\git.exe" : "git");

var RunGit = new Action<DirectoryPath, string> ((directory, arguments) =>
{
	StartProcess (GitToolPath, new ProcessSettings {
        Arguments = arguments,
        WorkingDirectory = directory,
    });
});

var RunNuGetRestore = new Action<FilePath> ((solution) =>
{
    NuGetRestore (solution, new NuGetRestoreSettings { 
        ToolPath = NugetToolPath
    });
});
var RunComponentRestore = new Action<FilePath> ((solution) =>
{
    RestoreComponents (solution, new XamarinComponentRestoreSettings { 
		ToolPath = XamarinComponentToolPath
    });
});
 
var PackageNuGet = new Action<FilePath, DirectoryPath> ((nuspecPath, outputPath) =>
{
	// NuGet messes up path on mac, so let's add ./ in front twice
	var basePath = IsRunningOnUnix () ? "././" : "./";

	if (!DirectoryExists (outputPath)) {
		CreateDirectory (outputPath);
	}

    NuGetPack (nuspecPath, new NuGetPackSettings { 
        Verbosity = NuGetVerbosity.Detailed,
        OutputDirectory = outputPath,		
        BasePath = basePath,
        ToolPath = NugetToolPath
    });				
});

var RunLipo = new Action<DirectoryPath, FilePath, FilePath[]> ((directory, output, inputs) =>
{
    if (!IsRunningOnUnix ()) {
        throw new InvalidOperationException ("lipo is only available on Unix.");
    }
    
    var dir = directory.CombineWithFilePath (output).GetDirectory ();
    if (!DirectoryExists (dir)) {
        CreateDirectory (dir);
    }

	var inputString = string.Join(" ", inputs.Select (i => string.Format ("\"{0}\"", i)));
	StartProcess ("lipo", new ProcessSettings {
		Arguments = string.Format("-create -output \"{0}\" {1}", output, inputString),
		WorkingDirectory = directory,
	});
});

var BuildXCode = new Action<FilePath, string, DirectoryPath, TargetOS> ((project, libraryTitle, workingDirectory, os) =>
{
    if (!IsRunningOnUnix ()) {
        return;
    }
    
    var fatLibrary = string.Format("lib{0}.a", libraryTitle);

	var output = string.Format ("lib{0}.a", libraryTitle);
	var i386 = string.Format ("lib{0}-i386.a", libraryTitle);
	var x86_64 = string.Format ("lib{0}-x86_64.a", libraryTitle);
	var armv7 = string.Format ("lib{0}-armv7.a", libraryTitle);
	var armv7s = string.Format ("lib{0}-armv7s.a", libraryTitle);
	var arm64 = string.Format ("lib{0}-arm64.a", libraryTitle);
	
	var buildArch = new Action<string, string, FilePath> ((sdk, arch, dest) => {
		if (!FileExists (dest)) {
            XCodeBuild (new XCodeBuildSettings {
                Project = workingDirectory.CombineWithFilePath (project).ToString (),
                Target = libraryTitle,
                Sdk = sdk,
                Arch = arch,
                Configuration = "Release",
            });
            var outputPath = workingDirectory.Combine ("build").Combine (os == TargetOS.Mac ? "Release" : ("Release-" + sdk)).CombineWithFilePath (output);
            CopyFile (outputPath, dest);
        }
	});
	
    if (os == TargetOS.Mac) {
        // not supported anymore
        // buildArch ("macosx", "i386", workingDirectory.CombineWithFilePath (i386));
        buildArch ("macosx", "x86_64", workingDirectory.CombineWithFilePath (x86_64));
        
		if (!FileExists (workingDirectory.CombineWithFilePath (fatLibrary))) {
            RunLipo (workingDirectory, fatLibrary, new [] {
                (FilePath)x86_64 
            });
        }
    } else if (os == TargetOS.iOS) {
        buildArch ("iphonesimulator", "i386", workingDirectory.CombineWithFilePath (i386));
        buildArch ("iphonesimulator", "x86_64", workingDirectory.CombineWithFilePath (x86_64));
        
        buildArch ("iphoneos", "armv7", workingDirectory.CombineWithFilePath (armv7));
        buildArch ("iphoneos", "armv7s", workingDirectory.CombineWithFilePath (armv7s));
        buildArch ("iphoneos", "arm64", workingDirectory.CombineWithFilePath (arm64));
        
		if (!FileExists (workingDirectory.CombineWithFilePath (fatLibrary))) {
            RunLipo (workingDirectory, fatLibrary, new [] {
                (FilePath)i386, 
                (FilePath)x86_64, 
                (FilePath)armv7, 
                (FilePath)armv7s, 
                (FilePath)arm64
            });
        }
    } else if (os == TargetOS.tvOS) {
        buildArch ("appletvsimulator", "x86_64", workingDirectory.CombineWithFilePath (x86_64));
        
        buildArch ("appletvos", "arm64", workingDirectory.CombineWithFilePath (arm64));
        
		if (!FileExists (workingDirectory.CombineWithFilePath (fatLibrary))) {
            RunLipo (workingDirectory, fatLibrary, new [] {
                (FilePath)x86_64, 
                (FilePath)arm64
            });
        }
    }
});
var DownloadPod = new Action<DirectoryPath, string, string, IDictionary<string, string>> ((podfilePath, platform, platformVersion, pods) => 
{
    if (!IsRunningOnUnix ()) {
        return;
    }
    
    if (!FileExists (podfilePath.CombineWithFilePath ("Podfile.lock"))) {
        var builder = new StringBuilder ();
        builder.AppendFormat ("platform :{0}, '{1}'", platform, platformVersion);
        builder.AppendLine ();
        if (CocoaPodVersion (new CocoaPodSettings ()) >= new System.Version (1, 0)) {
            builder.AppendLine ("install! 'cocoapods', :integrate_targets => false");
        }
        builder.AppendLine ("target 'Xamarin' do");
        foreach (var pod in pods) {
            builder.AppendFormat ("  pod '{0}', '{1}'", pod.Key, pod.Value);
            builder.AppendLine ();
        }
        builder.AppendLine ("end");
        
        if (!DirectoryExists (podfilePath)) {
            CreateDirectory (podfilePath);
        }
        
        System.IO.File.WriteAllText (podfilePath.CombineWithFilePath ("Podfile").ToString (), builder.ToString ());
	
        CocoaPodInstall (podfilePath);
    }
});
var CreateStaticPod = new Action<DirectoryPath, string, string, string, string, string> ((path, osxVersion, iosVersion, tvosVersion, name, version) => {
    if (osxVersion != null) {
        DownloadPod (path.Combine("osx"), 
                    "osx", osxVersion, 
                    new Dictionary<string, string> { { name, version } });
        BuildXCode ("Pods/Pods.xcodeproj", 
                    name,
                    path.Combine ("osx"),
                    TargetOS.Mac);
    }
    if (iosVersion != null) {
        DownloadPod (path.Combine("ios"), 
                    "ios", iosVersion, 
                    new Dictionary<string, string> { { name, version } });
        BuildXCode ("Pods/Pods.xcodeproj", 
                    name,
                    path.Combine ("ios"),
                    TargetOS.iOS);
    }
    if (tvosVersion != null) {
        DownloadPod (path.Combine("tvos"), 
                    "tvos", tvosVersion, 
                    new Dictionary<string, string> { { name, version } });
        BuildXCode ("Pods/Pods.xcodeproj", 
                    name,
                    path.Combine ("tvos"),
                    TargetOS.tvOS);
    }
});

var DownloadJar = new Action<string, string, string> ((source, destination, version) =>
{
    source = string.Format("http://search.maven.org/remotecontent?filepath=" + source, version);
    FilePath dest = string.Format(destination, version);
    DirectoryPath destDir = dest.GetDirectory ();
    if (!DirectoryExists (destDir))
        CreateDirectory (destDir);
    if (!FileExists (dest)) {
        DownloadFile (source, dest);
    }
});
var CheckoutGit = new Action<string, string, string> ((source, destination, version) =>
{
    if (!IsRunningOnUnix ()) {
        return;
    }

    DirectoryPath dest = MakeAbsolute ((DirectoryPath) destination);

    if (!DirectoryExists (destination)) {
        RunGit (".", "clone " + source + " " + dest);
    }

    RunGit (destination, "--git-dir=.git checkout " + version);
});

var Build = new Action<FilePath> ((solution) =>
{
    if (IsRunningOnWindows ()) {
        MSBuild (solution, s => s.SetConfiguration ("Release").SetMSBuildPlatform (MSBuildPlatform.x86));
    } else {
        XBuild (solution, s => s.SetConfiguration ("Release"));
    }
});

void MergeDirectory(DirectoryPath source, DirectoryPath dest, bool replace)
{
    var sourceDirName = source.FullPath;
    var destDirName = dest.FullPath;
    
    if (!DirectoryExists(source)) {
        throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + source);
    }

    if (!DirectoryExists(dest)) {
        CreateDirectory(dest);
    }

    DirectoryInfo dir = new DirectoryInfo(sourceDirName);
    
    FileInfo[] files = dir.GetFiles();
    foreach (FileInfo file in files) {
        string temppath = dest.CombineWithFilePath(file.Name).FullPath;
        if (FileExists(temppath)) {
            if (replace) {
                DeleteFile(temppath);
            }
        } else {
            file.CopyTo(temppath);
        }
    }

    DirectoryInfo[] dirs = dir.GetDirectories();
    foreach (DirectoryInfo subdir in dirs) {
        string temppath = dest.Combine(subdir.Name).FullPath;
        MergeDirectory(subdir.FullName, temppath, replace);
    }
}


////////////////////////////////////////////////////////////////////////////////////////////////////
// EXTERNALS - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("externals")
    .Does (() => 
{
    if (ForWindows) {
        DownloadJar ("com/vk/androidsdk/{0}/androidsdk-{0}.aar", "externals/android/vk.aar", vk_android_version);
    }
    if (ForMac) {       
        CreateStaticPod ("externals/", null, "6.0", null, "VK-ios-sdk", vk_ios_version);
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// LIBS - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("libs")
    .IsDependentOn ("externals")
    .Does (() => 
{
    FilePath solution = "./source/VKontakte.sln";
    if (ForWindowsOnly) {
        solution = "./source/VKontakte.Windows.sln";
    } else if (ForMacOnly) {
        solution = "./source/VKontakte.Mac.sln";
    }
	RunNuGetRestore (solution);
    Build (solution);
    
    var outputs = new List<string> ();
    if (ForWindows) {
        outputs.Add ("VKontakte.Android/bin/Release/VKontakte.Android.dll");
    }
    if (ForMac) {
        outputs.Add ("VKontakte.iOS/bin/Release/VKontakte.iOS.dll");
    }
    
    foreach (var output in outputs) {
        CopyFileToDirectory ("./source/" + output, "./output/");
    }
    
    CopyFileToDirectory ("README.md", "./output/");
    CopyFileToDirectory ("LICENSE.txt", "./output/");
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// PACKAGING - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("nuget")
    .IsDependentOn ("libs")
    .Does (() => 
{
    DeleteFiles ("./output/*.nupkg");
    var nugets = new List<string> ();
    if (ForWindows) {
        nugets.Add ("./nuget/Xamarin.VKontakte.nuspec");
    }
    foreach (var nuget in nugets) {
        PackageNuGet (nuget, "./output/");
    }
});

Task ("component")
    .IsDependentOn ("nuget")
    .Does (() => 
{
    // DeleteFiles ("./output/*.xam");
    // var yamls = new List<string> ();
    // if (ForWindows) {
    //     yamls.Add ("./component/square.androidtimessquare");
    // }
    // if (ForMac) {
    //     yamls.Add ("./component/square.aardvark");
    // }
    // foreach (DirectoryPath yaml in yamls) {
    //     PackageComponent (yaml, new XamarinComponentSettings { 
    //         ToolPath = XamarinComponentToolPath
    //     });
    //     MoveFiles (yaml + "/*.xam", "./output/");
    // }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// SAMPLES - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("samples")
    .IsDependentOn ("libs")
    .Does (() => 
{
    var samples = new List<string> ();
    if (ForWindows) {
        samples.Add ("./samples/VKontakteSampleAndroid/VKontakteSampleAndroid.sln");
    }
    if (ForMac) {
        samples.Add ("./samples/VKontakteSampleiOS/VKontakteSampleiOS.sln");
    }
    foreach (var sample in samples) {
		RunNuGetRestore (sample);
        Build (sample);
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// CLEAN - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("clean")
    .Does (() => 
{
    CleanDirectories ("./source/*/bin");
    CleanDirectories ("./source/*/obj");
    CleanDirectories ("./source/packages");
    CleanDirectories ("./source/Components");

    CleanDirectories ("./samples/*/bin");
    CleanDirectories ("./samples/*/obj");
    CleanDirectories ("./samples/packages");
    CleanDirectories ("./samples/Components");

    CleanDirectories ("./output");
});

Task ("clean-native")
    .IsDependentOn ("clean")
    .Does (() => 
{
    CleanDirectories("./externals");
});

//////////////////////////////////////////////////////////////////////
// TEMPORARY ARTIFACT MANAGEMENT
//////////////////////////////////////////////////////////////////////

Task("DownloadArtifacts")
    .WithCriteria(!string.IsNullOrEmpty(sha))
    .WithCriteria(!ForEverywhere)
    .Does(() =>
{
    if (ForWindowsOnly) {
        Information("Connecting to GitHub...");
        var client = new GitHubClient(new ProductHeaderValue("CrossPlatformBuild"));
        client.Credentials = new Credentials(GitHubToken);
        
        Information("Loading releases...");
        var releases = client.Release.GetAll(GitHubUser, GitHubRepository).Result;
        var releaseId = releases.Single(r => r.TagName == GitHubBuildTag).Id;
        
        Information("Loading CI release...");
        Release release = null;
        ReleaseAsset asset = null;
        var waitSeconds = 0;
        while (asset == null) {
            release = client.Release.Get(GitHubUser, GitHubRepository, releaseId).Result;
            Information("Loading asset...");
            asset = release.Assets.SingleOrDefault(a => a.Name == TemporaryArtifactsFilename);
            if (asset == null) {
                // only try for 15 minutes
                if (waitSeconds > 15 * 60) {
                    throw new Exception("Unable to download assets, maybe the build has failed.");
                }
                Information("Asset not found, waiting another 30 seconds.");
                waitSeconds += 30;
                System.Threading.Thread.Sleep(1000 * 30);
            }
        }
        Information("Found asset: {0}", asset.Id);
        Information("Url: {0}", asset.BrowserDownloadUrl);
        
        Information("Downloading asset...");
        if (FileExists(outputZip)) {
            DeleteFile(outputZip);
        }
        var url = string.Format("https://api.github.com/repos/{0}/{1}/releases/assets/{2}?access_token={3}", GitHubUser, GitHubRepository, asset.Id, GitHubToken);
        var wc = new WebClient();
        wc.Headers.Add("Accept", "application/octet-stream");
        wc.Headers.Add("User-Agent", "CrossPlatformBuild");
        wc.DownloadFile(url, outputZip.FullPath);
        
        Information("Extracting output...");
        DirectoryPath tmp = "./temp-output/";
        if (DirectoryExists(tmp)) {
            CleanDirectory(tmp);
        } else {
            CreateDirectory(tmp);
        }
        Unzip(outputZip, tmp);
        MergeDirectory(tmp, outDir, false);
    }
});

Task("UploadArtifacts")
    .WithCriteria(!string.IsNullOrEmpty(sha))
    .WithCriteria(!ForEverywhere)
    .Does(() =>
{
    Information("Connecting to GitHub...");
    var client = new GitHubClient(new ProductHeaderValue("CrossPlatformBuild"));
    client.Credentials = new Credentials(GitHubToken);

    Information("Loading releases...");
    var releases = client.Release.GetAll(GitHubUser, GitHubRepository).Result;
    var releaseId = releases.Single(r => r.TagName == GitHubBuildTag).Id;

    Information("Loading CI release...");
    var release = client.Release.Get(GitHubUser, GitHubRepository, releaseId).Result;

    Information("Loading asset...");
    var asset = release.Assets.SingleOrDefault(a => a.Name == TemporaryArtifactsFilename);
    
    if (asset != null) {
        Information("Deleting asset...");
        client.Release.DeleteAsset(GitHubUser, GitHubRepository, asset.Id).Wait();
    } else {
        Information("Asset not found.");
    }

    if (ForMacOnly) {
        Information("Compressing output...");
        if (FileExists(outputZip)) {
            DeleteFile(outputZip);
        }
        Zip(outDir, outputZip);

        Information("Creating asset...");
        var archiveContents = System.IO.File.OpenRead(outputZip.FullPath);
        var assetUpload = new ReleaseAssetUpload {
            FileName = TemporaryArtifactsFilename,
            ContentType = "application/zip",
            RawData = archiveContents
        };
        
        Information("Uploading asset...");
        asset = client.Release.UploadAsset(release, assetUpload).Result;
        Information("Uploaded asset: {0}", asset.Id);
        Information("Url: {0}", asset.BrowserDownloadUrl);
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// START - 
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("CI")
    .IsDependentOn ("externals")
    .IsDependentOn ("libs")
    .IsDependentOn ("nuget")
    .IsDependentOn ("component")
    .IsDependentOn ("samples")
    .Does (() => 
{
});

Task("Default")
    .IsDependentOn("externals")
    .IsDependentOn("libs")
    .IsDependentOn("DownloadArtifacts") // download late as each package is platform-specific
    .IsDependentOn("nuget")
    .IsDependentOn("component")
    .IsDependentOn("UploadArtifacts") // upload early so the other build can start
    .IsDependentOn("samples");

RunTarget (target);
