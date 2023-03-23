using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering;
//using Unity.Rendering;

namespace LimWorks.Rendering.ScreenSpaceReflections
{
    public struct ScreenSpaceReflectionsSettings
    {
        public float StepStrideLength;
        public float MaxSteps;
        public uint Downsample;
        public float MinSmoothness;
    }
    [ExecuteAlways]
    public class LimSSR : ScriptableRendererFeature
    {
        public static ScreenSpaceReflectionsSettings GetSettings()
        {
            return new ScreenSpaceReflectionsSettings()
            {
                Downsample = ssrFeatureInstance.Settings.downSample,
                MaxSteps = ssrFeatureInstance.Settings.maxSteps,
                MinSmoothness = ssrFeatureInstance.Settings.minSmoothness,
                StepStrideLength = ssrFeatureInstance.Settings.stepStrideLength,
            };
        }
        public static bool Enabled { get; set; } = true;
        public static void SetSettings(ScreenSpaceReflectionsSettings screenSpaceReflectionsSettings)
        {
            ssrFeatureInstance.Settings = new SSRSettings()
            {
                stepStrideLength = Mathf.Clamp(screenSpaceReflectionsSettings.StepStrideLength, 0.001f, float.MaxValue),
                maxSteps = screenSpaceReflectionsSettings.MaxSteps,
                downSample = (uint)Mathf.Clamp(screenSpaceReflectionsSettings.Downsample,0,2),
                minSmoothness = Mathf.Clamp01(screenSpaceReflectionsSettings.MinSmoothness),
                SSRShader = ssrFeatureInstance.Settings.SSRShader,
                SSR_Instance = ssrFeatureInstance.Settings.SSR_Instance,
            };
        }

        [ExecuteAlways]
        public class SsrPass : ScriptableRenderPass
        {
            public RenderTargetIdentifier Source { get; internal set; }
            RTHandle ReflectionMap;
            int reflectionMapID;
            RTHandle tempRenderTarget;
            int tempRenderID;

            internal SSRSettings Settings { get; set; }
            float downScaledX;
            float downScaledY;

            public float RenderScale { get; set; }
            public float ScreenHeight { get; set; }
            public float ScreenWidth { get; set; }
            float Scale => Settings.downSample + 1;

            //static RenderTexture tempSource;

            public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                base.Configure(cmd, cameraTextureDescriptor);
                ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Normal);

                cameraTextureDescriptor.colorFormat = RenderTextureFormat.DefaultHDR;
                cameraTextureDescriptor.mipCount = 8;
                cameraTextureDescriptor.autoGenerateMips = true;
                cameraTextureDescriptor.useMipMap = true;

                ReflectionMap = RTHandles.Alloc("_ReflectedColorMap", name: "_ReflectedColorMap");
                reflectionMapID = Shader.PropertyToID(ReflectionMap.name);
                //ReflectionMap.Init("_ReflectedColorMap");

                float downScaler = Scale;
                downScaledX = (ScreenWidth / (float)(downScaler));
                downScaledY = (ScreenHeight / (float)(downScaler));
                cmd.GetTemporaryRT(reflectionMapID, Mathf.CeilToInt(downScaledX), Mathf.CeilToInt(downScaledY), 0, FilterMode.Point, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default, 1, false);

                //duplicate source
                tempRenderTarget = RTHandles.Alloc("_MainTex", name: "_MainTex");
                tempRenderID = Shader.PropertyToID(tempRenderTarget.name);
                cmd.GetTemporaryRT(tempRenderID, cameraTextureDescriptor, FilterMode.Trilinear);
                cmd.Blit(Source, tempRenderID);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer commandBuffer = CommandBufferPool.Get("Screen space reflections");

                //calculate reflection
                commandBuffer.Blit(Source, reflectionMapID, Settings.SSR_Instance, 0);

                //compose reflection with main texture
                commandBuffer.Blit(Source, tempRenderID);
                commandBuffer.Blit(tempRenderID, Source, Settings.SSR_Instance, 1);
                context.ExecuteCommandBuffer(commandBuffer);

                CommandBufferPool.Release(commandBuffer);
            }
            public override void FrameCleanup(CommandBuffer cmd)
            {
                cmd.ReleaseTemporaryRT(tempRenderID);
                cmd.ReleaseTemporaryRT(reflectionMapID);
            }

        }

        [System.Serializable]
        internal class SSRSettings
        {
            [Min(0.001f)]
            [Tooltip("Raymarch step length (IMPACTS VISUAL QUALITY)")]
            public float stepStrideLength = .03f;
            [Tooltip("Maximum length of a raycast (IMPACTS PERFORMANCE) ")]
            public float maxSteps = 128;
            [Tooltip("1 / (value + 1) = resolution scale")]
            [Range(0,2)]
            public uint downSample = 0;
            [Min(0)]
            [Tooltip("Minimum smoothness value to have ssr work")]
            public float minSmoothness = 0.5f;

            [HideInInspector] public Material SSR_Instance;
            [HideInInspector] public Shader SSRShader;
        }

        SsrPass renderPass = null;
        internal static LimSSR ssrFeatureInstance;
        [SerializeField] SSRSettings Settings = new SSRSettings();

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!renderingData.cameraData.postProcessEnabled || !Enabled)
            {
                return;
            }

            if(!GetMaterial())
            {
                Debug.LogError("Cannot find ssr shader!");
                return;
            }

#if UNITY_2022_1_OR_NEWER
#else
            SetMaterialProperties(in renderingData);
            renderPass.Source = renderer.cameraColorTarget;
#endif
            Settings.SSR_Instance.SetVector("_WorldSpaceViewDir", renderingData.cameraData.camera.transform.forward);

            renderingData.cameraData.camera.depthTextureMode |= (DepthTextureMode.MotionVectors | DepthTextureMode.Depth | DepthTextureMode.DepthNormals);
            float renderscale = renderingData.cameraData.isSceneViewCamera ? 1 : renderingData.cameraData.renderScale;

            renderPass.RenderScale = renderscale;
            renderPass.ScreenHeight = renderingData.cameraData.camera.pixelHeight * renderscale;
            renderPass.ScreenWidth = renderingData.cameraData.camera.pixelWidth * renderscale;

            Settings.SSR_Instance.SetFloat("stride", Settings.stepStrideLength);
            Settings.SSR_Instance.SetFloat("numSteps", Settings.maxSteps);
            Settings.SSR_Instance.SetFloat("minSmoothness", Settings.minSmoothness);
            renderer.EnqueuePass(renderPass);
        }

#if UNITY_2022_1_OR_NEWER
        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (!renderingData.cameraData.postProcessEnabled || !Enabled)
            {
                return;
            }

            if (!GetMaterial())
            {
                Debug.LogError("Cannot find ssr shader!");
                return;
            }

            SetMaterialProperties(in renderingData);
            renderPass.Source = renderer.cameraColorTargetHandle;
        }
#endif
        //Called from SetupRenderPasses in urp 13+ (2022.1+). called from AddRenderPasses in URP 12 (2021.3)
        void SetMaterialProperties(in RenderingData renderingData)
        {
            var projectionMatrix = renderingData.cameraData.GetGPUProjectionMatrix();
            var viewMatrix = renderingData.cameraData.GetViewMatrix();

#if UNITY_EDITOR
            if (renderingData.cameraData.isSceneViewCamera)
            {
                Settings.SSR_Instance.SetFloat("_RenderScale", 1);
            }
            else
            {
                Settings.SSR_Instance.SetFloat("_RenderScale", renderingData.cameraData.renderScale);
            }
#else
            Settings.SSRFragmentShader.SetFloat("_RenderScale", renderingData.cameraData.renderScale);
#endif
            Settings.SSR_Instance.SetMatrix("_InverseProjectionMatrix", projectionMatrix.inverse);
            Settings.SSR_Instance.SetMatrix("_ProjectionMatrix", projectionMatrix);
            Settings.SSR_Instance.SetMatrix("_InverseViewMatrix", viewMatrix.inverse);
            Settings.SSR_Instance.SetMatrix("_ViewMatrix", viewMatrix);
        }


        class PersistantRT
        {
            public RenderTexture[] rt { get; private set; }
            public float previousScreenWidth { get; private set; }
            public float previousScreenHeight { get; private set; }

            int amount;

            public bool HasNull()
            {
                for (int i = 0; i < amount; i++)
                {
                    if (rt[i] == null)
                    {
                        return true;
                    }
                }
                return false;
            }

            public void ReleaseRt()
            {
                for (int i = 0; i < amount; i++)
                {
                    if (rt[i] != null)
                    {
                        rt[i].Release();
                    }
                }
            }
            IList<RenderTextureFormat> renderTextureFormats;
            ~PersistantRT()
            {
                ReleaseRt();
            }
            public PersistantRT(int amount = 1, IList<RenderTextureFormat> textureFormats = null)
            {
                Debug.Log("creating rt");

                this.amount = amount;
                this.renderTextureFormats = textureFormats;
                rt = new RenderTexture[amount];
                for (int i = 0; i < amount; i++)
                {
                    if (textureFormats != null)
                    {
                        rt[i] = new RenderTexture(Screen.width, Screen.height, 0, textureFormats[i]);
                    }
                    else
                    {
                        rt[i] = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.DefaultHDR);
                    }
                    rt[i].enableRandomWrite = true;
                }
            }
            public void Tick(float screenWidth, float screenHeight)
            {
                if (previousScreenWidth != screenWidth || previousScreenHeight != screenHeight)
                {
                    Debug.Log("readjusting rt");
                    ReleaseRt();
                    for (int i = 0; i < amount; i++)
                    {
                        if (renderTextureFormats != null)
                        {
                            rt[i] = new RenderTexture((int)screenWidth, (int)screenHeight, 0, renderTextureFormats[i]);
                        }
                        else
                        {
                            rt[i] = new RenderTexture((int)screenWidth, (int)screenHeight, 0, RenderTextureFormat.DefaultHDR);
                        }
                        rt[i].enableRandomWrite = true;
                    }
                    previousScreenWidth = screenWidth;
                    previousScreenHeight = screenHeight;
                }
            }
        }

        private bool GetMaterial()
        {
            if (Settings.SSR_Instance != null)
            {
                return true;
            }

            if (Settings.SSRShader == null)
            {
                Settings.SSRShader = Shader.Find("Hidden/ssr_shader");
                if (Settings.SSRShader == null)
                {
                    return false;
                }
            }

            Settings.SSR_Instance = CoreUtils.CreateEngineMaterial(Settings.SSRShader);

            return Settings.SSR_Instance != null;
        }
        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(Settings.SSR_Instance);
        }
        public override void Create()
        {
            ssrFeatureInstance = this;
            renderPass = new SsrPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
                Settings = this.Settings
            };
            GetMaterial();
        }
    }
}
