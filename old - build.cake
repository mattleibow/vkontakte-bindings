#addin "Cake.Xamarin"
#addin "Cake.XCode"

#load "../../common.cake"

var VK_ANDROID_URL = "http://search.maven.org/remotecontent?filepath=com/vk/androidsdk/1.6.2/androidsdk-1.6.2.aar";

CakeSpec.Libs = new ISolutionBuilder [] {
	new IOSSolutionBuilder {
		SolutionPath = "./source/ios/VKontakte.iOS.sln",
		OutputFiles = new [] { 
			new OutputFileCopy {
				FromFile = "./source/ios/VKontakte.iOS/bin/Release/VKontakte.iOS.dll",
			}
		}
	},
	new DefaultSolutionBuilder {
		SolutionPath = "./source/android/VKontakte.Android.sln",
		OutputFiles = new [] { 
			new OutputFileCopy {
				FromFile = "./source/android/VKontakte.Android/bin/Release/VKontakte.Android.dll",
			}
		}
	}
};

CakeSpec.Samples = new ISolutionBuilder [] {
	new IOSSolutionBuilder { SolutionPath = "./samples/ios/VKontakteSampleiOS.sln" },	
	new DefaultSolutionBuilder { SolutionPath = "./samples/android/VKontakteSampleAndroid.sln" }
};

Task ("externals-android")
	.WithCriteria (!FileExists ("./externals/android/vk.aar"))
	.Does (() => 
{
	if (!DirectoryExists ("./externals/android"))
		CreateDirectory ("./externals/android");

	DownloadFile (VK_ANDROID_URL, "./externals/android/vk.aar");
});
Task ("externals-ios").Does (() => 
{
	if (!DirectoryExists ("./externals/ios"))
		CreateDirectory ("./externals/ios");

	CreatePodfile ("./externals/ios", "ios", "6.0", new Dictionary<string, string> {
        { "VK-ios-sdk", "1.3.7" }
	});
	
	if (!FileExists ("./externals/ios/Podfile.lock")) {
		CocoaPodInstall ("./externals/ios", new CocoaPodInstallSettings { NoIntegrate = true });
	}

	BuildXCode ("Pods/Pods.xcodeproj", "VK-ios-sdk", workingDirectory: "./externals/ios");
	CopyFile ("./externals/ios/libVK-ios-sdk.a", "./externals/ios/libVKontakte.a");
});
Task ("externals").IsDependentOn ("externals-android").IsDependentOn ("externals-ios");


Task ("clean").IsDependentOn ("clean-base").Does (() => 
{
	if (DirectoryExists ("./externals/android"))
		DeleteDirectory ("./externals/android", true);

	if (DirectoryExists ("./externals/ios"))
		DeleteDirectory ("./externals/ios", true);
});

DefineDefaultTasks ();

RunTarget (TARGET);
