using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

[assembly:AssemblyVersion("0.0902")]
namespace Scatterer
{
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class Scatterer: MonoBehaviour
    {    
        private static Scatterer instance;
        public static Scatterer Instance {get {return instance;}}

        public MainSettingsReadWrite mainSettings = new MainSettingsReadWrite();
        public PluginDataReadWrite pluginData     = new PluginDataReadWrite();
        public ConfigReader planetsConfigsReader  = new ConfigReader ();

        public GUIhandler guiHandler = new GUIhandler();
        
        public ScattererCelestialBodiesManager scattererCelestialBodiesManager = new ScattererCelestialBodiesManager ();
        public SunflareManager sunflareManager; GameObject sunflareManagerGO;
        public EVEReflectionHandler EveReflectionHandler;
        private Coroutine cloudReappliedCoroutine;
        //public PlanetshineManager planetshineManager;

        DisableAmbientLight ambientLightScript;
        
        public ShadowRemoveFadeCommandBuffer shadowFadeRemover;
        public TweakShadowCascades shadowCascadeTweaker;

        //probably move these to buffer rendering manager
        DepthToDistanceCommandBuffer farDepthCommandbuffer, nearDepthCommandbuffer;
        public DepthPrePassMerger nearDepthPassMerger;
        
        public Light sunLight, scaledSpaceSunLight, mainMenuLight, ivaLight;
        public Light[] lights;
        public Camera farCamera, scaledSpaceCamera, nearCamera;
        static float originalShadowDistance = 0f;

        //classic SQUAD
        ReflectionProbeChecker reflectionProbeChecker;
        GameObject ReflectionProbeCheckerGO;
        
        bool coreInitiated = false;
        public bool isActive = false;
        public bool unifiedCameraMode = false;
        public string versionNumber = "0.0902";

        public List<GenericAntiAliasing> antiAliasingScripts = new List<GenericAntiAliasing>();

        void Awake ()
        {
            // Editor scene transitions are additive, do this check and early exit first to avoid checking instance multiple times
            isActive = HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER ||
                        HighLogic.LoadedScene == GameScenes.TRACKSTATION || HighLogic.LoadedScene == GameScenes.MAINMENU;

            if (!isActive)
            {
                UnityEngine.Object.Destroy(this);
                return;
            }

            if (instance == null)
            {
                instance = this;
                Utils.LogDebug("Core instance created");
            }
            else
            {
                // Destroy any duplicate instances that may be created by a duplicate install
                Utils.LogError("Destroying duplicate instance, check your install for duplicate scatterer folders, or nested GameData folders");                
            }

            Utils.LogInfo ("Version:"+versionNumber);
            Utils.LogInfo ("Running on: " + SystemInfo.graphicsDeviceVersion + " on " +SystemInfo.operatingSystem);
            Utils.LogInfo ("Game resolution: " + Screen.width.ToString() + "x" +Screen.height.ToString());
            Utils.LogInfo ("Compute shader support: " + SystemInfo.supportsComputeShaders.ToString());
            Utils.LogInfo ("Async GPU readback support: " + SystemInfo.supportsAsyncGPUReadback.ToString());

            LoadSettings ();
            scattererCelestialBodiesManager.Init ();
            guiHandler.Init();

            if (mainSettings.useOceanShaders)
            {
                OceanUtils.removeStockOceansIfNotDone();
            }
            else
            {
                OceanUtils.restoreOceansIfNeeded();
            }

            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                if (mainSettings.integrateWithEVEClouds)
                {
                    ShaderReplacer.Instance.ReplaceEVEshaders();
                }
            }

            StartCoroutine (DelayedInit ());

            // The built-in AA breaks basically all post effects
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                QualitySettings.antiAliasing = 0;
            }
        }

        //wait for 5 frames for EVE, TUFX and the game to finish setting up
        IEnumerator DelayedInit()
        {
            int delayFrames = 5;
            for (int i=0; i<delayFrames; i++)
                yield return new WaitForFixedUpdate ();

            Init();
        }

        void Init()
        {
            SetupMainCameras ();

            FindSunlights ();

            SetShadows();
            
            Utils.FixKopernicusRingsRenderQueue ();            
            Utils.FixSunsCoronaRenderQueue ();

            AddReflectionProbeFixer ();

//            if (mainSettings.usePlanetShine)
//            {
//                planetshineManager = new PlanetshineManager();
//            }

            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                // copy stock depth buffers and combine into a single depth buffer
                if (!unifiedCameraMode && (mainSettings.useOceanShaders || mainSettings.fullLensFlareReplacement))
                {
                    farDepthCommandbuffer = farCamera.gameObject.AddComponent<DepthToDistanceCommandBuffer>();
                    nearDepthCommandbuffer = nearCamera.gameObject.AddComponent<DepthToDistanceCommandBuffer>();
                }
            }

            // TODO: move all AA logic to a separate class?
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                //cleanup any forgotten/glitched AA scripts
                foreach (GenericAntiAliasing antiAliasing in Resources.FindObjectsOfTypeAll(typeof(GenericAntiAliasing)))
                {
                    if (antiAliasing)
                    {
                        Component.Destroy(antiAliasing);
                    }
                }

                if (mainSettings.useSubpixelMorphologicalAntialiasing)
                {
                    SubpixelMorphologicalAntialiasing nearAA = nearCamera.gameObject.AddComponent<SubpixelMorphologicalAntialiasing>();
                    antiAliasingScripts.Add(nearAA);
                    
                    // On camera change change apply to new camera
                    GameEvents.OnCameraChange.Add(SMAAOnCameraChange);
                }

                if (mainSettings.useTemporalAntiAliasing)
                {
                    ShaderReplacer.Instance.LoadedShaders.TryGetValue("Scatterer/Internal-MotionVectors", out Shader customMotionVectorsShader);

                    // The custom motion vectors shader fixes NaN motion vectors on the scaled camera's skybox and combines the motion vectors of all cameras
                    if (customMotionVectorsShader != null)
                    {
                        GraphicsSettings.SetShaderMode(BuiltinShaderType.MotionVectors, BuiltinShaderMode.UseCustom);
                        GraphicsSettings.SetCustomShader(BuiltinShaderType.MotionVectors, customMotionVectorsShader);
                    }

                    TemporalAntiAliasing nearAA, farAA, scaledAA;

                    nearAA = nearCamera.gameObject.AddComponent<TemporalAntiAliasing>();
                    nearAA.checkOceanDepth = mainSettings.useOceanShaders;
                    nearAA.resetMotionVectors = false;
                    antiAliasingScripts.Add(nearAA);

                    if (!unifiedCameraMode && farCamera)
                    {
                        farAA = farCamera.gameObject.AddComponent<TemporalAntiAliasing>();
                        farAA.checkOceanDepth = mainSettings.useOceanShaders;
                        farAA.resetMotionVectors = false;
                        antiAliasingScripts.Add(farAA);
                    }

                    // doesn't seem to hurt performance more
                    scaledAA = scaledSpaceCamera.gameObject.AddComponent<TemporalAntiAliasing>();
                    scaledAA.jitterTransparencies = true;
                    antiAliasingScripts.Add(scaledAA);

                    if (!mainSettings.useSubpixelMorphologicalAntialiasing)
                    {
                        GameEvents.OnCameraChange.Add(AddTAAToInternalCamera);
                    }
                }
                
                if(mainSettings.mergeDepthPrePass)
                {
                    Utils.LogInfo("Adding nearDepthPassMerger");
                    nearDepthPassMerger = (DepthPrePassMerger) nearCamera.gameObject.AddComponent<DepthPrePassMerger>();
                }
            }

            if ((mainSettings.fullLensFlareReplacement) && (HighLogic.LoadedScene != GameScenes.MAINMENU))
            {
                sunflareManagerGO = new GameObject("Scatterer sunflare manager");
                sunflareManager = sunflareManagerGO.AddComponent<SunflareManager>();
                sunflareManager.Init();
            }

            if (mainSettings.integrateWithEVEClouds)
            {
                EveReflectionHandler = new EVEReflectionHandler();
                EveReflectionHandler.Start();
            }

            if (mainSettings.disableAmbientLight && !ambientLightScript)
            {
                ambientLightScript = (DisableAmbientLight) scaledSpaceCamera.gameObject.AddComponent (typeof(DisableAmbientLight));
            }

            if (!unifiedCameraMode)
                shadowFadeRemover = (ShadowRemoveFadeCommandBuffer)nearCamera.gameObject.AddComponent (typeof(ShadowRemoveFadeCommandBuffer));

            //magically fix stupid issues when reverting to space center from map view
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                MapView.MapIsEnabled = false;
            }

            GameEvents.OnCameraChange.Add(RegisterIVACameraAndLightForSunlightModulator);

            coreInitiated = true;

            Utils.LogDebug("Core setup done");
        }

        void Update()
        {
            guiHandler.UpdateGUIvisible ();

            //TODO: get rid of this check, maybe move to coroutine? what happens when coroutine exits?
            if (coreInitiated)
            {
                // The built-in AA breaks basically all post effects
                if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER)
                {
                    QualitySettings.antiAliasing = 0;
                }

                scattererCelestialBodiesManager.Update ();

                /*
                //move this out of this update, let it be a one time thing
                //TODO: check what this means
                if (bufferManager)
                {
                    if (!bufferManager.depthTextureCleared && (MapView.MapIsEnabled || !scattererCelestialBodiesManager.isPQSEnabledOnScattererPlanet) )
                        bufferManager.ClearDepthTexture();
                }
                */

                if (sunflareManager != null)
                {
                    sunflareManager.UpdateFlares();
                }

//                if(planetshineManager != null)
//                {
//                    planetshineManager.UpdatePlanetshine();
//                }
            }
        } 

        void OnDestroy ()
        {
            GameEvents.OnCameraChange.Remove(SMAAOnCameraChange);
            GameEvents.OnCameraChange.Remove(AddTAAToInternalCamera);
            GameEvents.OnCameraChange.Remove(RegisterIVACameraAndLightForSunlightModulator);

            if (isActive)
            {
//                if(planetshineManager != null)
//                {
//                    planetshineManager.Cleanup();
//                }

                if (scattererCelestialBodiesManager != null)
                {
                    scattererCelestialBodiesManager.Cleanup();
                }

                if (ambientLightScript)
                {
                    ambientLightScript.Cleanup();
                    Component.Destroy(ambientLightScript);
                }                

                if (nearCamera)
                {
                    if (nearCamera.gameObject.GetComponent (typeof(Wireframe)))
                        Component.Destroy (nearCamera.gameObject.GetComponent (typeof(Wireframe)));
                    
                    if (farCamera && farCamera.gameObject.GetComponent (typeof(Wireframe)))
                        Component.Destroy (farCamera.gameObject.GetComponent (typeof(Wireframe)));
                    
                    if (scaledSpaceCamera.gameObject.GetComponent (typeof(Wireframe)))
                        Component.Destroy (scaledSpaceCamera.gameObject.GetComponent (typeof(Wireframe)));
                }

                if (sunflareManager)
                {
                    UnityEngine.Component.Destroy(sunflareManager);
                    GameObject.Destroy(sunflareManagerGO);
                }

                if (shadowFadeRemover)
                {
                    Component.Destroy(shadowFadeRemover);
                }

                if (shadowCascadeTweaker)
                {
                    Component.Destroy(shadowCascadeTweaker);
                }

                if (farDepthCommandbuffer)
                    Component.Destroy (farDepthCommandbuffer);
                
                if (nearDepthCommandbuffer)
                    Component.Destroy (nearDepthCommandbuffer);

                if (nearDepthPassMerger)
                    Component.Destroy (nearDepthPassMerger);

                foreach (GenericAntiAliasing antiAliasing in antiAliasingScripts)
                {
                    if (antiAliasing)
                    {
                        Component.Destroy(antiAliasing);
                    }
                }

                if (reflectionProbeChecker)
                {
                    Component.Destroy (reflectionProbeChecker);
                }

                if (ReflectionProbeCheckerGO)
                {
                    UnityEngine.GameObject.Destroy (ReflectionProbeCheckerGO);
                }

                QualitySettings.antiAliasing = GameSettings.ANTI_ALIASING;

                if (EveReflectionHandler != null)
                    EveReflectionHandler.CleanUp();

                pluginData.inGameWindowLocation=new Vector2(guiHandler.windowRect.x,guiHandler.windowRect.y);
                SaveSettings();
            }

            UnityEngine.Object.Destroy (guiHandler);
            
        }

        void OnGUI ()
        {
            guiHandler.DrawGui ();
        }
        
        public void LoadSettings ()
        {
            mainSettings.loadMainSettings ();
            pluginData.loadPluginData ();
            planetsConfigsReader.loadConfigs ();

            // HACK: for mainMenu everything is jumbled, so just attempt to load every planet always
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                foreach (ScattererCelestialBody _SCB in planetsConfigsReader.scattererCelestialBodies)
                {
                    _SCB.loadDistance = Mathf.Infinity;
                    _SCB.unloadDistance = Mathf.Infinity;
                }
            }
        }
        
        public void SaveSettings ()
        {
            pluginData.savePluginData ();
            mainSettings.saveMainSettingsIfChanged ();
        }

        void SetupMainCameras()
        {
            scaledSpaceCamera = Camera.allCameras.FirstOrDefault (_cam => _cam.name == "Camera ScaledSpace");
            farCamera = Camera.allCameras.FirstOrDefault (_cam => _cam.name == "Camera 01");
            nearCamera = Camera.allCameras.FirstOrDefault (_cam => _cam.name == "Camera 00");

            if (nearCamera && !farCamera) 
            {
                Utils.LogInfo("Running in unified camera mode");
                unifiedCameraMode = true;
            }

            if (scaledSpaceCamera && nearCamera)
            {
                if (mainSettings.terrainShadows)
                {
                    if (!unifiedCameraMode && (mainSettings.dualCamShadowCascadeSplitsOverride != Vector3.zero))
                    {
                        shadowCascadeTweaker = (TweakShadowCascades) Utils.getEarliestLocalCamera().gameObject.AddComponent(typeof(TweakShadowCascades));
                        shadowCascadeTweaker.Init(mainSettings.dualCamShadowCascadeSplitsOverride);
                    }
                    else if (unifiedCameraMode && (mainSettings.unifiedCamShadowCascadeSplitsOverride != Vector3.zero))
                    {
                        shadowCascadeTweaker = (TweakShadowCascades) Utils.getEarliestLocalCamera().gameObject.AddComponent(typeof(TweakShadowCascades));
                        shadowCascadeTweaker.Init(mainSettings.unifiedCamShadowCascadeSplitsOverride);
                    }
                }
                
                if (mainSettings.overrideNearClipPlane)
                {
                    Utils.LogDebug("Override near clip plane from:"+nearCamera.nearClipPlane.ToString()+" to:"+mainSettings.nearClipPlane.ToString());
                    nearCamera.nearClipPlane = mainSettings.nearClipPlane;
                }

                SunlightModulatorsManager.AddRenderingHookToCamera(nearCamera);
                SunlightModulatorsManager.AddRenderingHookToCamera(farCamera);
                SunlightModulatorsManager.AddResetHookToCamera(scaledSpaceCamera);
            }
            else if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                // If are in main menu, where there is only 1 camera, affect all cameras to Landscape camera
                scaledSpaceCamera = Camera.allCameras.Single(_cam  => _cam.name == "Landscape Camera");
                farCamera = scaledSpaceCamera;
                nearCamera = scaledSpaceCamera;
            }
            else if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
            {
                // If in trackstation, just to get rid of some nullrefs
                farCamera = scaledSpaceCamera;
                nearCamera = scaledSpaceCamera;
            }
        }

        void SetShadows()
        {
            if (HighLogic.LoadedScene != GameScenes.MAINMENU)
            {
                if (unifiedCameraMode && (mainSettings.d3d11ShadowFix || mainSettings.terrainShadows))
                {
                    QualitySettings.shadowProjection = ShadowProjection.StableFit; //way more resistant to jittering
                    GraphicsSettings.SetShaderMode (BuiltinShaderType.ScreenSpaceShadows, BuiltinShaderMode.UseCustom);

                    GraphicsSettings.SetCustomShader (BuiltinShaderType.ScreenSpaceShadows, ShaderReplacer.Instance.LoadedShaders [("Scatterer/customScreenSpaceShadows")]);
                }

                if (mainSettings.shadowsOnOcean || mainSettings.oceanLightRays)
                {
                    if (unifiedCameraMode || SystemInfo.graphicsDeviceVersion.Contains("Direct3D 11.0"))
                    {
                        QualitySettings.shadowProjection = ShadowProjection.StableFit;    //StableFit + splitSpheres is the only thing that works Correctly for unified camera (dx11) ocean shadows
                                                                                          //Otherwise we get artifacts near shadow cascade edges
                    }
                    else
                    {
                        QualitySettings.shadowProjection = ShadowProjection.CloseFit;    //CloseFit without SplitSpheres seems to be the only setting that works for OpenGL for ocean shadows
                                                                                        //Seems like I lack the correct variables to determine which shadow path to take
                                                                                        //also try without the transparent tag
                    }
                }

                if (mainSettings.terrainShadows)
                {
                    if (originalShadowDistance == 0f)
                    {
                        originalShadowDistance = QualitySettings.shadowDistance;
                    }

                    QualitySettings.shadowDistance = unifiedCameraMode ? mainSettings.unifiedCamShadowsDistance : mainSettings.dualCamShadowsDistance;
                    Utils.LogDebug ("Set shadow distance: " + QualitySettings.shadowDistance.ToString ());
                    Utils.LogDebug ("Number of shadow cascades detected " + QualitySettings.shadowCascades.ToString ());

                    SetShadowsForLight (sunLight);

                    // And finally force shadow Casting and receiving on celestial bodies if not already set
                    foreach (CelestialBody _sc in FlightGlobals.Bodies)
                    {
                        if (_sc.pqsController)
                        {
                            _sc.pqsController.meshCastShadows = true;
                            _sc.pqsController.meshRecieveShadows = true;
                        }
                    }
                }
                else
                {
                    DisableCustomShadowResForLight (sunLight);

                    if (originalShadowDistance != 0f)
                    {
                        Utils.LogDebug("Restore original shadow distance: "+originalShadowDistance.ToString());
                        QualitySettings.shadowDistance = originalShadowDistance;;
                        originalShadowDistance = 0f;
                    }
                }
            }
        }

        public void SetShadowsForLight (Light light)
        {
            if (light && mainSettings.terrainShadows && (HighLogic.LoadedScene != GameScenes.MAINMENU))
            {
                //fixes checkerboard artifacts aka shadow acne
                float bias = unifiedCameraMode ? mainSettings.unifiedCamShadowNormalBiasOverride : mainSettings.dualCamShadowNormalBiasOverride;
                float normalBias = unifiedCameraMode ? mainSettings.unifiedCamShadowBiasOverride : mainSettings.dualCamShadowBiasOverride;
                if (bias != 0f)
                    light.shadowBias = bias;
                if (normalBias != 0f)
                    light.shadowNormalBias = normalBias;
                int customRes = unifiedCameraMode ? mainSettings.unifiedCamShadowResolutionOverride : mainSettings.dualCamShadowResolutionOverride;
                if (customRes != 0)
                {
                    if (Utils.IsPowerOfTwo (customRes))
                    {
                        Utils.LogDebug ("Setting shadowmap resolution to: " + customRes.ToString () + " on " + light.name);
                        light.shadowCustomResolution = customRes;
                    }
                    else
                    {
                        Utils.LogError ("Selected shadowmap resolution not a power of 2: " + customRes.ToString ());
                    }
                }
                else
                    light.shadowCustomResolution = 0;
            }
        }

        public void DisableCustomShadowResForLight (Light light)
        {
            if (light && !mainSettings.terrainShadows && (HighLogic.LoadedScene != GameScenes.MAINMENU))
            {
                light.shadowCustomResolution = 0;
            }
        }

        void FindSunlights ()
        {
            lights = (Light[])Light.FindObjectsOfType (typeof(Light));
            foreach (Light _light in lights)
            {
                if (_light.gameObject.name == "SunLight")
                {
                    sunLight = _light;
                }
                if (_light.gameObject.name == "Scaledspace SunLight")
                {
                    scaledSpaceSunLight = _light;
                }
                if (_light.gameObject.name.Contains ("PlanetLight") || _light.gameObject.name.Contains ("Directional light"))
                {
                    mainMenuLight = _light;
                }
            }
        }

        // TODO: this shouldn't be here
        public void TriggerOnCloudReapplied()
        {
            if (cloudReappliedCoroutine != null)
                StopCoroutine(cloudReappliedCoroutine);

            cloudReappliedCoroutine = StartCoroutine(DelayedOnCloudsReapplied());
        }

        IEnumerator DelayedOnCloudsReapplied()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            if (EveReflectionHandler != null)
                EveReflectionHandler.OnCloudsReapplied();
        }

        public void OnRenderTexturesLost()
        {
            foreach (ScattererCelestialBody _cur in planetsConfigsReader.scattererCelestialBodies)
            {
                if (_cur.active)
                {
                    _cur.prolandManager.skyNode.ReInitMaterialUniformsOnRenderTexturesLoss ();
                    if (_cur.prolandManager.hasOcean && mainSettings.useOceanShaders && !_cur.prolandManager.skyNode.inScaledSpace)
                    {
                        _cur.prolandManager.RebuildOcean ();
                    }
                }
            }
        }

        // Just a dummy gameObject so the reflectionProbeChecker can capture the reflection Camera
        public void AddReflectionProbeFixer()
        {
            ReflectionProbeCheckerGO = new GameObject ("Scatterer ReflectionProbeCheckerGO");
            //ReflectionProbeCheckerGO.transform.parent = nearCamera.transform; //VesselViewer doesn't like this for some reason
            ReflectionProbeCheckerGO.layer = 15;

            reflectionProbeChecker = ReflectionProbeCheckerGO.AddComponent<ReflectionProbeChecker> ();

            MeshFilter _mf = ReflectionProbeCheckerGO.AddComponent<MeshFilter> ();
            _mf.mesh.Clear ();
            _mf.mesh = MeshFactory.MakePlane (2, 2, MeshFactory.PLANE.XY, false, false);
            _mf.mesh.bounds = new Bounds (Vector4.zero, new Vector3 (Mathf.Infinity, Mathf.Infinity, Mathf.Infinity));

            MeshRenderer _mr = ReflectionProbeCheckerGO.AddComponent<MeshRenderer> ();
            _mr.sharedMaterial = new Material (ShaderReplacer.Instance.LoadedShaders[("Scatterer/invisible")]);
            _mr.material = new Material (ShaderReplacer.Instance.LoadedShaders[("Scatterer/invisible")]);
            _mr.receiveShadows = false;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.enabled = true;
        }
        
        public void AddTAAToInternalCamera(CameraManager.CameraMode cameraMode)
        {
            if (cameraMode == CameraManager.CameraMode.IVA)
            {
                Camera internalCamera = Camera.allCameras.FirstOrDefault (_cam => _cam.name == "InternalCamera");
                if (internalCamera)
                {
                    TemporalAntiAliasing internalTAA = internalCamera.GetComponent<TemporalAntiAliasing>();
                    if(internalTAA == null)
                    {
                        internalTAA = internalCamera.gameObject.AddComponent<TemporalAntiAliasing>();
                        internalTAA.resetMotionVectors = false;
                        antiAliasingScripts.Add(internalTAA);
                    }
                }
            }
        }

        public void SMAAOnCameraChange(CameraManager.CameraMode cameraMode)
        {
            if (cameraMode == CameraManager.CameraMode.IVA)
            {
                Camera internalCamera = Camera.allCameras.FirstOrDefault (_cam => _cam.name == "InternalCamera");
                if (internalCamera)
                {
                    // Add depth-based SMAA to internal camera, to avoid blurring over cockpit elements and text especially with custom IVAs
                    SubpixelMorphologicalAntialiasing internalSMAA = internalCamera.gameObject.AddComponent<SubpixelMorphologicalAntialiasing>();
                    internalSMAA.forceDepthBuffermode();
                    antiAliasingScripts.Add(internalSMAA);
                }
            }
            else
            {
                var ivaSmaaScripts = antiAliasingScripts.OfType<SubpixelMorphologicalAntialiasing>()
                    .Where(x => x.QualityUsed == SubpixelMorphologicalAntialiasing.Quality.DepthMode).ToList();

                antiAliasingScripts.RemoveAll(script => ivaSmaaScripts.Contains(script));

                foreach (var ivaSmaaScript in ivaSmaaScripts)
                {
                    Component.Destroy(ivaSmaaScript);
                }
            }
        }

        public void RegisterIVACameraAndLightForSunlightModulator(CameraManager.CameraMode cameraMode)
        {
            if (cameraMode == CameraManager.CameraMode.IVA && InternalCamera.Instance != null)
            {
                var ivaSun = InternalSpace.Instance.transform.Find("IVASun");

                if (ivaSun != null)
                {
                    ivaLight = ivaSun.GetComponent<Light>();
                }

                Camera internalCamera = InternalCamera.Instance.GetComponentInChildren<Camera>();
                SunlightModulatorsManager.AddRenderingHookToCamera(internalCamera);
            }
        }

    }
}
