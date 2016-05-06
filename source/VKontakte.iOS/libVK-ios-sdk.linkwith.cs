using ObjCRuntime;

[assembly: LinkWith ("libVK-ios-sdk.a", 
	Frameworks = "Foundation UIKit SafariServices CoreGraphics Security",
    LinkTarget = LinkTarget.Arm64 | LinkTarget.ArmV7 | LinkTarget.ArmV7s | LinkTarget.Simulator | LinkTarget.Simulator64,
    ForceLoad = true)]
