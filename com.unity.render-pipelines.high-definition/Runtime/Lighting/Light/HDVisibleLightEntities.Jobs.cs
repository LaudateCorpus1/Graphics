using System;
using UnityEngine.Jobs;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Threading;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityEngine.Rendering.HighDefinition
{
    internal partial class HDVisibleLightEntities
    {
        JobHandle m_ProcessVisibleLightJobHandle;

#if ENABLE_BURST_1_5_0_OR_NEWER
        [Unity.Burst.BurstCompile]
#endif
        struct ProcessVisibleLightJob : IJobParallelFor
        {
            #region Light entity SoA data
            [ReadOnly]
            public NativeArray<float3> lightPositions;
            [ReadOnly]
            public NativeArray<HDAdditionalLightData.PointLightHDType> pointLightTypes;
            [ReadOnly]
            public NativeArray<SpotLightShape> spotLightShapes;
            [ReadOnly]
            public NativeArray<AreaLightShape> areaLightShapes;
            [ReadOnly]
            public NativeArray<float> fadeDistances;
            [ReadOnly]
            public NativeArray<float> volumetricFadeDistances;
            [ReadOnly]
            public NativeArray<bool> includeForRayTracings;
            [ReadOnly]
            public NativeArray<bool> useScreenSpaceShadows;
            [ReadOnly]
            public NativeArray<bool> useRayTracedShadows;
            [ReadOnly]
            public NativeArray<float> lightDimmer;
            [ReadOnly]
            public NativeArray<float> volumetricDimmer;
            [ReadOnly]
            public NativeArray<float> shadowDimmer;
            [ReadOnly]
            public NativeArray<float> shadowFadeDistance;
            [ReadOnly]
            public NativeArray<bool> affectDiffuse;
            [ReadOnly]
            public NativeArray<bool> affectSpecular;
            #endregion

            #region Visible light SoA
            [ReadOnly]
            public NativeArray<VisibleLight> visibleLights;
            [ReadOnly]
            public NativeArray<HDLightEntityData> visibleEntities;
            [ReadOnly]
            public NativeArray<LightBakingOutput> visibleLightBakingOutput;
            [ReadOnly]
            public NativeArray<LightShadows> visibleLightShadows;
            #endregion

            #region Parameters
            [ReadOnly]
            public float3 cameraPosition;
            [ReadOnly]
            public int pixelCount;
            [ReadOnly]
            public bool enableAreaLights;
            [ReadOnly]
            public bool enableRayTracing;
            [ReadOnly]
            public bool showDirectionalLight;
            [ReadOnly]
            public bool showPunctualLight;
            [ReadOnly]
            public bool showAreaLight;
            [ReadOnly]
            public bool enableShadowMaps;
            [ReadOnly]
            public bool enableScreenSpaceShadows;
            [ReadOnly]
            public int maxDirectionalLightsOnScreen;
            [ReadOnly]
            public int maxPunctualLightsOnScreen;
            [ReadOnly]
            public int maxAreaLightsOnScreen;
            [ReadOnly]
            public DebugLightFilterMode debugFilterMode;
            #endregion

            #region output processed lights
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<int> processedVisibleLightCountsPtr;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<HDLightType>     processedLightTypes;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<LightCategory>   processedLightCategories;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<GPULightType>    processedGPULightType;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<LightVolumeType> processedLightVolumeType;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<float> processedLightDistanceToCamera;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<float> processedLightDistanceFade;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<float> processedLightVolumetricDistanceFade;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<bool>  processedLightIsBakedShadowMask;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<HDVisibleLightEntities.ShadowMapFlags>  processedShadowMapFlags;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<ProcessedVisibleLightEntity>  processedEntities;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint>   sortKeys;
            [WriteOnly]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<int>   shadowLightsDataIndices;
            #endregion

            private bool TrivialRejectLight(in VisibleLight light, in HDLightEntityData lightEntity)
            {
                if (!lightEntity.valid)
                    return true;

                // We can skip the processing of lights that are so small to not affect at least a pixel on screen.
                // TODO: The minimum pixel size on screen should really be exposed as parameter, to allow small lights to be culled to user's taste.
                const int minimumPixelAreaOnScreen = 1;
                if ((light.screenRect.height * light.screenRect.width * pixelCount) < minimumPixelAreaOnScreen)
                    return true;

                return false;
            }

            private int IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots counterSlot)
            {
                int outputIndex = 0;
                unsafe
                {
                    int* ptr = (int*)processedVisibleLightCountsPtr.GetUnsafePtr<int>() + (int)counterSlot;
                    outputIndex = Interlocked.Increment(ref UnsafeUtility.AsRef<int>(ptr));
                }
                return outputIndex;
            }

            private int DecrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots counterSlot)
            {
                int outputIndex = 0;
                unsafe
                {
                    int* ptr = (int*)processedVisibleLightCountsPtr.GetUnsafePtr<int>() + (int)counterSlot;
                    outputIndex = Interlocked.Decrement(ref UnsafeUtility.AsRef<int>(ptr));
                }
                return outputIndex;
            }

            private int NextOutputIndex() => IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.ProcessedLights) - 1;

            private bool IncrementLightCounterAndTestLimit(LightCategory lightCategory, GPULightType gpuLightType)
            {
                // Do NOT process lights beyond the specified limit!
                switch (lightCategory)
                {
                    case LightCategory.Punctual:
                        if (gpuLightType == GPULightType.Directional) // Our directional lights are "punctual"...
                        {
                            var directionalLightcount = IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.DirectionalLights) - 1;
                            if (!showDirectionalLight || directionalLightcount >= maxDirectionalLightsOnScreen)
                            {
                                DecrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.DirectionalLights);
                                return false;
                            }
                            break;
                        }
                        var punctualLightcount = IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.PunctualLights) - 1;
                        if (!showPunctualLight || punctualLightcount >= maxPunctualLightsOnScreen)
                        {
                            DecrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.PunctualLights);
                            return false;
                        }
                        break;
                    case LightCategory.Area:
                        var areaLightCount = IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.AreaLightCounts) - 1;
                        if (!showAreaLight || areaLightCount >= maxAreaLightsOnScreen)
                        {
                            DecrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.AreaLightCounts);
                            return false;
                        }
                        break;
                    default:
                        break;
                }

                return true;
            }

            private HDVisibleLightEntities.ShadowMapFlags EvaluateShadowState(
                LightShadows shadows,
                HDLightType lightType,
                GPULightType gpuLightType,
                AreaLightShape areaLightShape,
                bool useScreenSpaceShadowsVal,
                bool useRayTracingShadowsVal,
                float shadowDimmerVal,
                float shadowFadeDistanceVal,
                float distanceToCamera,
                LightVolumeType lightVolumeType)
            {
                var flags = HDVisibleLightEntities.ShadowMapFlags.None;
                bool willRenderShadowMap = shadows != LightShadows.None && enableShadowMaps;
                if (!willRenderShadowMap)
                    return flags;

                // When creating a new light, at the first frame, there is no AdditionalShadowData so we can't really render shadows
                if (shadowDimmerVal <= 0)
                    return flags;

                // If the shadow is too far away, we don't render it
                bool isShadowInRange = lightType == HDLightType.Directional || distanceToCamera < shadowFadeDistanceVal;
                if (!isShadowInRange)
                    return flags;

                if (lightType == HDLightType.Area && areaLightShape != AreaLightShape.Rectangle)
                    return flags;

                // First we reset the ray tracing and screen space shadow data
                flags |= HDVisibleLightEntities.ShadowMapFlags.WillRenderShadowMap;

                // If this camera does not allow screen space shadows we are done, set the target parameters to false and leave the function
                if (!enableScreenSpaceShadows)
                    return flags;

                // Flag the ray tracing only shadows
                if (enableRayTracing && useRayTracingShadowsVal)
                {
                    bool validShadow = false;
                    if (gpuLightType == GPULightType.Point
                        || gpuLightType == GPULightType.Rectangle
                        || (gpuLightType == GPULightType.Spot && lightVolumeType == LightVolumeType.Cone))
                        validShadow = true;

                    if (validShadow)
                        flags |= HDVisibleLightEntities.ShadowMapFlags.WillRenderScreenSpaceShadow
                            | HDVisibleLightEntities.ShadowMapFlags.WillRenderRayTracedShadow;
                }

                // Flag the directional shadow
                if (useScreenSpaceShadowsVal && gpuLightType == GPULightType.Directional)
                {
                    flags |= HDVisibleLightEntities.ShadowMapFlags.WillRenderScreenSpaceShadow;
                    if (enableRayTracing && useRayTracingShadowsVal)
                        flags |= HDVisibleLightEntities.ShadowMapFlags.WillRenderRayTracedShadow;
                }

                return flags;
            }

            public void Execute(int index)
            {
                VisibleLight visibleLight = visibleLights[index];
                HDLightEntityData visibleLightEntity = visibleEntities[index];
                LightBakingOutput bakingOutput = visibleLightBakingOutput[index];
                LightShadows shadows = visibleLightShadows[index];
                if (TrivialRejectLight(visibleLight, visibleLightEntity))
                    return;

                int dataIndex = visibleLightEntity.dataIndex;

                if (enableRayTracing && !includeForRayTracings[dataIndex])
                    return;

                float distanceToCamera = math.distance(cameraPosition, lightPositions[visibleLightEntity.dataIndex]);
                var lightType = HDAdditionalLightData.TranslateLightType(visibleLight.lightType, pointLightTypes[dataIndex]);
                var lightCategory = LightCategory.Count;
                var gpuLightType = GPULightType.Point;
                var areaLightShape = areaLightShapes[dataIndex];

                if (!enableAreaLights && (lightType == HDLightType.Area && (areaLightShape == AreaLightShape.Rectangle || areaLightShape == AreaLightShape.Tube)))
                    return;

                var spotLightShape = spotLightShapes[dataIndex];
                var lightVolumeType = LightVolumeType.Count;
                var isBakedShadowMaskLight =
                    bakingOutput.lightmapBakeType == LightmapBakeType.Mixed &&
                    bakingOutput.mixedLightingMode == MixedLightingMode.Shadowmask &&
                    bakingOutput.occlusionMaskChannel != -1;    // We need to have an occlusion mask channel assign, else we have no shadow mask
                HDRenderPipeline.EvaluateGPULightType(lightType, spotLightShape, areaLightShape,
                    ref lightCategory, ref gpuLightType, ref lightVolumeType);

                if (debugFilterMode != DebugLightFilterMode.None && debugFilterMode.IsEnabledFor(gpuLightType, spotLightShape))
                    return;

                float lightDistanceFade = gpuLightType == GPULightType.Directional ? 1.0f : HDUtils.ComputeLinearDistanceFade(distanceToCamera, fadeDistances[dataIndex]);
                float volumetricDistanceFade = gpuLightType == GPULightType.Directional ? 1.0f : HDUtils.ComputeLinearDistanceFade(distanceToCamera, volumetricFadeDistances[dataIndex]);

                bool contributesToLighting = ((lightDimmer[dataIndex] > 0) && (affectDiffuse[dataIndex] || affectSpecular[dataIndex])) || (volumetricDimmer[dataIndex] > 0);
                contributesToLighting = contributesToLighting && (lightDistanceFade > 0);

                var shadowMapFlags = EvaluateShadowState(
                    shadows, lightType, gpuLightType, areaLightShape,
                    useScreenSpaceShadows[dataIndex], useRayTracedShadows[dataIndex],
                    shadowDimmer[dataIndex], shadowFadeDistance[dataIndex], distanceToCamera, lightVolumeType);

                if (!contributesToLighting)
                    return;

                if (!IncrementLightCounterAndTestLimit(lightCategory, gpuLightType))
                    return;

                int outputIndex = NextOutputIndex();
                uint sortKey = (uint)lightCategory << 27 | (uint)gpuLightType << 22 | (uint)lightVolumeType << 17 | (uint)index;
                sortKeys[outputIndex] = sortKey;

                processedLightVolumeType[index] = lightVolumeType;
                processedEntities[index] = new ProcessedVisibleLightEntity()
                {
                    dataIndex = visibleLightEntity.dataIndex,
                    gpuLightType = gpuLightType,
                    lightType = lightType,
                    lightDistanceFade = lightDistanceFade,
                    lightVolumetricDistanceFade = volumetricDistanceFade,
                    distanceToCamera = distanceToCamera,
                    shadowMapFlags = shadowMapFlags,
                    isBakedShadowMask = isBakedShadowMaskLight
                };

                if ((shadowMapFlags & HDVisibleLightEntities.ShadowMapFlags.WillRenderShadowMap) != 0)
                {
                    int shadowOutputIndex = IncrementCounter(HDVisibleLightEntities.ProcessLightsCountSlots.ShadowLights) - 1;
                    shadowLightsDataIndices[shadowOutputIndex] = index;
                }
            }
        }

        public void StartProcessVisibleLightJob(
            HDCamera hdCamera,
            NativeArray<VisibleLight> visibleLights,
            in GlobalLightLoopSettings lightLoopSettings,
            DebugDisplaySettings debugDisplaySettings)
        {
            if (m_Size == 0)
                return;

            if (!m_ProcessVisibleLightCounts.IsCreated)
            {
                int totalCounts = Enum.GetValues(typeof(ProcessLightsCountSlots)).Length;
                m_ProcessVisibleLightCounts.ResizeArray(totalCounts);
            }

            for (int i = 0; i < m_ProcessVisibleLightCounts.Length; ++i)
                m_ProcessVisibleLightCounts[i] = 0;

            var lightEntityCollection = HDLightEntityCollection.instance;
            var processVisibleLightJob = new ProcessVisibleLightJob()
            {
                //Parameters.
                cameraPosition = hdCamera.camera.transform.position,
                pixelCount     = hdCamera.actualWidth * hdCamera.actualHeight,
                enableAreaLights = ShaderConfig.s_AreaLights != 0,
                enableRayTracing = hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing),
                showDirectionalLight = debugDisplaySettings.data.lightingDebugSettings.showDirectionalLight,
                showPunctualLight = debugDisplaySettings.data.lightingDebugSettings.showPunctualLight,
                showAreaLight = debugDisplaySettings.data.lightingDebugSettings.showAreaLight,
                enableShadowMaps = hdCamera.frameSettings.IsEnabled(FrameSettingsField.ShadowMaps),
                enableScreenSpaceShadows = hdCamera.frameSettings.IsEnabled(FrameSettingsField.ScreenSpaceShadows),
                maxDirectionalLightsOnScreen = lightLoopSettings.maxDirectionalLightsOnScreen,
                maxPunctualLightsOnScreen = lightLoopSettings.maxPunctualLightsOnScreen,
                maxAreaLightsOnScreen = lightLoopSettings.maxAreaLightsOnScreen,
                debugFilterMode = debugDisplaySettings.GetDebugLightFilterMode(),

                //SoA of all light entities.
                lightPositions  = lightEntityCollection.lightPositions,
                pointLightTypes = lightEntityCollection.pointLightTypes,
                spotLightShapes = lightEntityCollection.spotLightShapes,
                areaLightShapes = lightEntityCollection.areaLightShapes,
                fadeDistances   = lightEntityCollection.fadeDistances,
                volumetricFadeDistances = lightEntityCollection.volumetricFadeDistances,
                includeForRayTracings = lightEntityCollection.includeForRayTracings,
                useScreenSpaceShadows = lightEntityCollection.useScreenSpaceShadows,
                useRayTracedShadows = lightEntityCollection.useRayTracedShadows,
                lightDimmer = lightEntityCollection.lightDimmer,
                volumetricDimmer = lightEntityCollection.volumetricDimmer,
                shadowDimmer = lightEntityCollection.shadowDimmer,
                shadowFadeDistance = lightEntityCollection.shadowFadeDistance,
                affectDiffuse = lightEntityCollection.affectDiffuse,
                affectSpecular = lightEntityCollection.affectSpecular,

                //SoA of all visible light entities.
                visibleLights   = visibleLights,
                visibleEntities = m_VisibleEntities,
                visibleLightBakingOutput = m_VisibleLightBakingOutput,
                visibleLightShadows = m_VisibleLightShadows,

                //Output processed lights.
                processedVisibleLightCountsPtr = m_ProcessVisibleLightCounts,
                processedLightVolumeType = m_ProcessedLightVolumeType,
                processedEntities = m_ProcessedEntities,
                sortKeys                       = m_SortKeys,
                shadowLightsDataIndices        = m_ShadowLightsDataIndices
            };

            m_ProcessVisibleLightJobHandle = processVisibleLightJob.Schedule(m_Size, 32);
        }

        public void CompleteProcessVisibleLightJob()
        {
            m_ProcessVisibleLightJobHandle.Complete();
        }
    }
}
