using System.Collections.Generic;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

/// <summary>One-click VR setup (D-234): OpenXR as the XR loader, the common controller profiles
/// enabled, and the game scene first in the build list.
///
/// <para><b>Why a menu item instead of hand-written settings assets.</b> XR Plug-in Management and
/// OpenXR keep their state in generated assets with sub-objects and GUIDs; writing those by hand
/// is how a project ends up with a loader that silently never starts. Driving the packages' own
/// APIs produces exactly what the Project Settings UI would.</para>
///
/// <para><b>Idempotent</b>: re-running it changes nothing that is already right, so it is also the
/// "fix VR" button if someone unticks a box in Project Settings before the fair.</para></summary>
public static class EsVRSetup
{
    const string GAME_SCENE = "Assets/trabalho_marqueto.unity";

    [MenuItem("Tools/Escape Room/VR/Configure VR (OpenXR)")]
    public static void Configure()
    {
        var perTarget = GetOrCreatePerTarget();

        ConfigureLoader(perTarget, BuildTargetGroup.Standalone);
        ConfigureOpenXR(BuildTargetGroup.Standalone);

        // Quest standalone (Android) only when that module is installed; PC VR through Link /
        // Air Link / SteamVR uses the Standalone settings above and needs nothing else.
        if (BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
        {
            ConfigureLoader(perTarget, BuildTargetGroup.Android);
            ConfigureOpenXR(BuildTargetGroup.Android);
        }
        else
            Debug.Log("[VR] Modulo Android nao instalado - configurado so PC VR (Quest via Link/Air Link, SteamVR).");

        EnsureGameSceneFirst();
        // D-236: a headset game must keep running when the PC window loses focus (Link dashboard,
        // SteamVR overlay, the operator clicking elsewhere) - otherwise the game pauses or input stops.
        PlayerSettings.runInBackground = true;
        AssetDatabase.SaveAssets();
        Debug.Log("[VR] OpenXR configurado. Conecte o headset (Quest Link / SteamVR) e de Play.");
    }

    /// <summary>Entry point for <c>-executeMethod</c> in batch mode.</summary>
    public static void ConfigureBatch()
    {
        Configure();
        EditorApplication.Exit(0);
    }

    static XRGeneralSettingsPerBuildTarget GetOrCreatePerTarget()
    {
        XRGeneralSettingsPerBuildTarget s;
        if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out s) && s != null) return s;

        if (!AssetDatabase.IsValidFolder("Assets/XR")) AssetDatabase.CreateFolder("Assets", "XR");
        s = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
        AssetDatabase.CreateAsset(s, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, s, true);
        return s;
    }

    static void ConfigureLoader(XRGeneralSettingsPerBuildTarget perTarget, BuildTargetGroup group)
    {
        if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
        if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

        var general = perTarget.SettingsForBuildTarget(group);
        // Without a headset the loader fails quietly and the game runs on desktop - so leaving
        // this on costs nothing and is what makes Play-in-headset work.
        general.InitManagerOnStart = true;
        EditorUtility.SetDirty(general);

        string loader = typeof(OpenXRLoader).FullName;
        if (!XRPackageMetadataStore.IsLoaderAssigned(loader, group))
        {
            bool ok = XRPackageMetadataStore.AssignLoader(perTarget.ManagerSettingsForBuildTarget(group), loader, group);
            Debug.Log("[VR] OpenXR loader para " + group + ": " + (ok ? "ok" : "FALHOU"));
        }
    }

    static void ConfigureOpenXR(BuildTargetGroup group)
    {
        FeatureHelpers.RefreshFeatures(group);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        if (settings == null)
        {
            Debug.LogError("[VR] OpenXRSettings ausente para " + group + " - rode o menu de novo apos a importacao.");
            return;
        }
        settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;

        // The controllers people actually bring: Quest (Touch / Touch Plus), Index, Vive, WMR,
        // and the Khronos simple profile as the floor every runtime supports.
        var wanted = new List<System.Type>
        {
            typeof(OculusTouchControllerProfile),
            typeof(MetaQuestTouchPlusControllerProfile),
            typeof(ValveIndexControllerProfile),
            typeof(HTCViveControllerProfile),
            typeof(MicrosoftMotionControllerProfile),
            typeof(KHRSimpleControllerProfile),
        };
        if (group == BuildTargetGroup.Android)
            wanted.Add(typeof(UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature));

        foreach (var t in wanted)
            foreach (var f in settings.GetFeatures(t))
                if (f != null && !f.enabled)
                {
                    f.enabled = true;
                    EditorUtility.SetDirty(f);
                    Debug.Log("[VR] " + group + ": " + f.name + " ligado");
                }
        EditorUtility.SetDirty(settings);
    }

    /// <summary>The build list only had SampleScene, so a player build would open an empty demo
    /// and REINICIAR (LoadScene(buildIndex)) would have nothing to reload.</summary>
    static void EnsureGameSceneFirst()
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == GAME_SCENE);
        scenes.Insert(0, new EditorBuildSettingsScene(GAME_SCENE, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
