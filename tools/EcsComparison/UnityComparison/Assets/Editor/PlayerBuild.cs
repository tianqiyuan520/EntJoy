using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>构建播放器形态的对照程序，供 --player-matrix / --struct 调用。</summary>
public static class PlayerBuild
{
    private const string ScenePath = "Assets/Scenes/Empty.unity";
    private const string OutputPath = "Builds/PlayerMatrix/PlayerMatrix.exe";
    private const string OutputPathIl2Cpp = "Builds/PlayerIL2CPP/PlayerIL2CPP.exe";

    /// <summary>Mono 后端，保留作 Editor 之外的第二口径。</summary>
    public static void BuildWindows()
    {
        Build(OutputPath, ScriptingImplementation.Mono2x, "PLAYER_BUILD");
    }

    /// <summary>IL2CPP 发布口径，Burst 生效。</summary>
    public static void BuildWindowsIl2Cpp()
    {
        Build(OutputPathIl2Cpp, ScriptingImplementation.IL2CPP, "PLAYER_BUILD_IL2CPP");
    }

    private static void Build(string outputPath, ScriptingImplementation backend, string tag)
    {
        try
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            if (EditorBuildSettings.scenes.Length == 0)
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            // 播放器默认开 Burst 安全校验；对照测量需要与 Editor 的发布口径一致。
            Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Burst.BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;

            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
            if (backend == ScriptingImplementation.IL2CPP)
                PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Low);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            bool ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"{tag} backend={backend} result={report.summary.result} bytes={report.summary.totalSize} errors={report.summary.totalErrors}");
            EditorApplication.Exit(ok ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
