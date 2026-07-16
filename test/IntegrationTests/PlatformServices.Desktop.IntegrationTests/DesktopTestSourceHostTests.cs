// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using AwesomeAssertions;

using Microsoft.VisualStudio.TestPlatform.MSTestAdapter.PlatformServices;
using Microsoft.VisualStudio.TestPlatform.MSTestAdapter.PlatformServices.Utilities;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;

using TestFramework.ForTestingMSTest;

namespace PlatformServices.Desktop.ComponentTests;

public class DesktopTestSourceHostTests : TestContainer
{
    private TestSourceHost? _testSourceHost;

    public void ParentDomainShouldHonorSearchDirectoriesSpecifiedInRunsettings()
    {
        string sampleProjectDirPath = Path.GetDirectoryName(GetTestAssemblyPath("SampleProjectForAssemblyResolution"));
        string runSettingsXml =
            $"""
             <RunSettings>
                <RunConfiguration>
                    <DisableAppDomain>True</DisableAppDomain>
                </RunConfiguration>
                <MSTestV2>
                    <AssemblyResolution>
                        <Directory path = " % Temp %\directory" includeSubDirectories = "true" />
                        <Directory path = "C:\windows" includeSubDirectories = "false" />
                        <Directory path = "{sampleProjectDirPath}" />
                    </AssemblyResolution>
                </MSTestV2>
             </RunSettings>
             """;

        LoadMSTestSettings(runSettingsXml);
        _testSourceHost = new TestSourceHost(
            GetTestAssemblyPath("DesktopTestProjectx86Debug"),
            runSettingsXml);
        _testSourceHost.SetupHost();

        // Loading SampleProjectForAssemblyResolution.dll should not throw.
        // It is present in  <Directory path = ".\ComponentTests" />  specified in runsettings
        Assembly.Load("SampleProjectForAssemblyResolution");
    }

    public void ChildDomainResolutionPathsShouldHaveSearchDirectoriesSpecifiedInRunsettings()
    {
        string sampleProjectPath = GetTestAssemblyPath("SampleProjectForAssemblyResolution");
        string sampleProjectDirPath = Path.GetDirectoryName(sampleProjectPath);
        string runSettingsXml =
            $"""
             <RunSettings>
               <RunConfiguration>
                 <DisableAppDomain>False</DisableAppDomain>
               </RunConfiguration>
               <MSTestV2>
                 <AssemblyResolution>
                   <Directory path = " % Temp %\directory" includeSubDirectories = "true" />
                   <Directory path = "C:\windows" includeSubDirectories = "false" />
                   <Directory path = "{sampleProjectDirPath}" />
                 </AssemblyResolution>
               </MSTestV2>
             </RunSettings>
             """;

        LoadMSTestSettings(runSettingsXml);
        _testSourceHost = new TestSourceHost(
            GetTestAssemblyPath("DesktopTestProjectx86Debug"),
            runSettingsXml);
        _testSourceHost.SetupHost();

        var asm = Assembly.LoadFrom(sampleProjectPath);
        Type type = asm.GetType("SampleProjectForAssemblyResolution.SerializableTypeThatShouldBeLoaded");

        // Creating instance of SampleProjectForAssemblyResolution should not throw.
        // It is present in  <Directory path = ".\ComponentTests" />  specified in runsettings
        AppDomainUtilities.CreateInstance(_testSourceHost.AppDomain!, type, null);
    }

    public void DisposeShouldUnloadChildAppDomain()
    {
        string testSourceHandler = GetTestAssemblyPath("DesktopTestProjectx86Debug");
        _testSourceHost = new TestSourceHost(testSourceHandler, null);
        _testSourceHost.SetupHost();

        // Check that child appdomain was indeed created
        _testSourceHost.AppDomain.Should().NotBeNull();
        _testSourceHost.Dispose();

        // Check that child-appdomain is now unloaded.
        _testSourceHost.AppDomain.Should().BeNull();
    }

    private static string GetArtifactsBinDir()
    {
        string artifactsBinDirPath = Path.GetFullPath(Path.Combine(
            typeof(DesktopTestSourceHostTests).Assembly.Location,
            "..",
            "..",
            "..",
            ".."));
        Directory.Exists(artifactsBinDirPath).Should().BeTrue($"artifacts bin dir '{artifactsBinDirPath}' should exist");

        return artifactsBinDirPath;
    }

    private static string GetTestAssemblyPath(string assetName)
    {
        string testAssetDir = Path.Combine(
            GetArtifactsBinDir(),
            assetName,
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net462");

        // Some test assets (e.g. DesktopTestProjectx86Debug) are built as Microsoft.Testing.Platform
        // executables (.exe) rather than libraries (.dll), while others (e.g.
        // SampleProjectForAssemblyResolution) remain libraries. Probe for both output extensions.
        string dllPath = Path.Combine(testAssetDir, assetName + ".dll");
        string exePath = Path.Combine(testAssetDir, assetName + ".exe");
        string testAssetPath = File.Exists(dllPath) ? dllPath : exePath;

        File.Exists(testAssetPath).Should().BeTrue($"Test asset '{dllPath}' or '{exePath}' should exist");

        return testAssetPath;
    }

    private static void LoadMSTestSettings(string runSettingsXml)
    {
        StringReader stringReader = new(runSettingsXml);
        var reader = XmlReader.Create(stringReader, XmlRunSettingsUtilities.ReaderSettings);
        MSTestSettingsProvider mstestSettingsProvider = new();
        reader.ReadToFollowing("MSTestV2");
        mstestSettingsProvider.Load(reader);
    }
}
