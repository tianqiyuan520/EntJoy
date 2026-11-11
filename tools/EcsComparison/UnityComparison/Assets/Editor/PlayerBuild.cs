using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>构建播放器形态的遍历对照程序，供 <c>--player-matrix</c> 调用。</summary>
public static class PlayerBuild
{
    private const string ScenePath = "Assets/Scenes/Empty.unity";
    private const string OutputPath = "Builds/PlayerMatrix/PlayerMatrix.exe";

    public static void BuildWindows()
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

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            // 播放器默认开 Burst 安全校验；对照测量需要与 Editor 的发布口径一致。
            Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Burst.BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            bool ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"PLAYER_BUILD result={report.summary.result} bytes={report.summary.totalSize} errors={report.summary.totalErrors}");
            EditorApplication.Exit(ok ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
