using System;
using UnityEngine.Jobs;
using Unity.Jobs;
using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering.HighDefinition
{
    internal partial class HDGpuLightList
    {
        JobHandle m_CreateGpuLightDataJobHandle;

        //#if ENABLE_BURST_1_5_0_OR_NEWER
        //        [Unity.Burst.BurstCompile]
        //#endif
        struct CreateGpuLightDataJob : IJobParallelFor
        {
            #region Parameters
            public bool lightLayersEnabled;
            #endregion

            #region input light entity SoA data
            [ReadOnly]
            public NativeArray<LightLayerEnum> lightLayers;
            [ReadOnly]
            public NativeArray<float> lightDimmer;
            [ReadOnly]
            public NativeArray<float> volumetricDimmer;
            [ReadOnly]
            public NativeArray<bool> affectDiffuse;
            [ReadOnly]
            public NativeArray<bool> affectSpecular;
            [ReadOnly]
            public NativeArray<bool> applyRangeAttenuation;
            [ReadOnly]
            public NativeArray<float> shadowFadeDistance;
            [ReadOnly]
            public NativeArray<float> shapeWidth;
            [ReadOnly]
            public NativeArray<float> shapeHeight;
            [ReadOnly]
            public NativeArray<float> aspectRatio;
            [ReadOnly]
            public NativeArray<float> innerSpotPercent;
            [ReadOnly]
            public NativeArray<float> spotIESCutoffPercent;
            [ReadOnly]
            public NativeArray<float> shapeRadius;
            [ReadOnly]
            public NativeArray<float> barnDoorLength;
            [ReadOnly]
            public NativeArray<float> barnDoorAngle;
            [ReadOnly]
            public NativeArray<Color> shadowTint;
            #endregion

            #region input visible lights processed
            [ReadOnly]
            public NativeArray<uint> sortKeys;
            [ReadOnly]
            public NativeArray<HDVisibleLightEntities.ProcessedVisibleLightEntity> processedEntities;
            [ReadOnly]
            public NativeArray<VisibleLight> visibleLights;
            #endregion

            #region output processed lights
            [WriteOnly]
            public NativeArray<LightData> lights;
            [WriteOnly]
            public NativeArray<Vector3> lightDimensionData;
            #endregion

            public void Execute(int index)
            {
                var sortKey = sortKeys[index];
                LightCategory lightCategory = (LightCategory)((sortKey >> 27) & 0x1F);
                GPULightType gpuLightType = (GPULightType)((sortKey >> 22) & 0x1F);
                LightVolumeType lightVolumeType = (LightVolumeType)((sortKey >> 17) & 0x1F);

                //TODO: have different sort keys for directional. Another set of jobs just for directional.
                if (gpuLightType ==  GPULightType.Directional)
                    return;

                int lightIndex = (int)(sortKey & 0xFFFF);
                var light = visibleLights[lightIndex];
                var processedEntity = processedEntities[lightIndex];
                int dataIndex = processedEntity.dataIndex;
                var lightData = new LightData();

                int lightLayerMaskValue = (int)lightLayers[dataIndex];
                uint lightLayerValue = lightLayerMaskValue < 0 ? (uint)LightLayerEnum.Everything : (uint)lightLayerMaskValue;
                lightData.lightLayers = lightLayersEnabled ? lightLayerValue : uint.MaxValue;
                lightData.lightType = gpuLightType;

                var visibleLightAxisAndPosition = light.GetAxisAndPosition();
                lightData.positionRWS = visibleLightAxisAndPosition.Position;
                lightData.range = light.range;

                if (applyRangeAttenuation[dataIndex])
                {
                    lightData.rangeAttenuationScale = 1.0f / (light.range * light.range);
                    lightData.rangeAttenuationBias = 1.0f;

                    if (lightData.lightType == GPULightType.Rectangle)
                    {
                        // Rect lights are currently a special case because they use the normalized
                        // [0, 1] attenuation range rather than the regular [0, r] one.
                        lightData.rangeAttenuationScale = 1.0f;
                    }
                }
                else
                {
                    // Solve f(x) = b - (a * x)^2 where x = (d/r)^2.
                    // f(0) = huge -> b = huge.
                    // f(1) = 0    -> huge - a^2 = 0 -> a = sqrt(huge).
                    const float hugeValue = 16777216.0f;
                    const float sqrtHuge = 4096.0f;
                    lightData.rangeAttenuationScale = sqrtHuge / (light.range * light.range);
                    lightData.rangeAttenuationBias = hugeValue;

                    if (lightData.lightType == GPULightType.Rectangle)
                    {
                        // Rect lights are currently a special case because they use the normalized
                        // [0, 1] attenuation range rather than the regular [0, r] one.
                        lightData.rangeAttenuationScale = sqrtHuge;
                    }
                }

                float shapeWidthVal = shapeWidth[dataIndex];
                float shapeHeightVal = shapeHeight[dataIndex];
                lightData.color = new Vector3(light.finalColor.r, light.finalColor.g, light.finalColor.b);
                lightData.forward = visibleLightAxisAndPosition.Forward;
                lightData.up = visibleLightAxisAndPosition.Up;
                lightData.right = visibleLightAxisAndPosition.Right;

                var lightDimensions = new Vector3(); // X = length or width, Y = height, Z = range (depth)
                lightDimensions.x = shapeWidthVal;
                lightDimensions.y = shapeHeightVal;
                lightDimensions.z = light.range;

                lightData.boxLightSafeExtent = 1.0f;

                if (lightData.lightType == GPULightType.ProjectorBox)
                {
                    // Rescale for cookies and windowing.
                    lightData.right *= 2.0f / Mathf.Max(shapeWidthVal, 0.001f);
                    lightData.up *= 2.0f / Mathf.Max(shapeHeightVal, 0.001f);
                    //TODO: do in a flat loop, since its dependent on shadow index.
                    // If we have shadows, we need to shrink the valid range so that we don't leak light due to filtering going out of bounds.
                    //if (shadowIndex >= 0)
                    //{
                    //    // We subtract a bit from the safe extent depending on shadow resolution
                    //    float shadowRes = additionalLightData.shadowResolution.Value(m_ShadowInitParameters.shadowResolutionPunctual);
                    //    shadowRes = Mathf.Clamp(shadowRes, 128.0f, 2048.0f); // Clamp in a somewhat plausible range.
                    //    // The idea is to subtract as much as 0.05 for small resolutions.
                    //    float shadowResFactor = Mathf.Lerp(0.05f, 0.01f, Mathf.Max(shadowRes / 2048.0f, 0.0f));
                    //    lightData.boxLightSafeExtent = 1.0f - shadowResFactor;
                    //}
                }
                else if (lightData.lightType == GPULightType.ProjectorPyramid)
                {
                    // Get width and height for the current frustum
                    var spotAngle = light.spotAngle;
                    float aspectRatioValue = aspectRatio[dataIndex];

                    float frustumWidth, frustumHeight;

                    if (aspectRatioValue >= 1.0f)
                    {
                        frustumHeight = 2.0f * Mathf.Tan(spotAngle * 0.5f * Mathf.Deg2Rad);
                        frustumWidth = frustumHeight * aspectRatioValue;
                    }
                    else
                    {
                        frustumWidth = 2.0f * Mathf.Tan(spotAngle * 0.5f * Mathf.Deg2Rad);
                        frustumHeight = frustumWidth / aspectRatioValue;
                    }

                    // Adjust based on the new parametrization.
                    lightDimensions.x = frustumWidth;
                    lightDimensions.y = frustumHeight;

                    //// Rescale for cookies and windowing.
                    lightData.right *= 2.0f / frustumWidth;
                    lightData.up *= 2.0f / frustumHeight;
                }
                
                if (lightData.lightType == GPULightType.Spot)
                {
                    var spotAngle = light.spotAngle;

                    var innerConePercent = innerSpotPercent[dataIndex] / 100.0f;
                    var cosSpotOuterHalfAngle = Mathf.Clamp(Mathf.Cos(spotAngle * 0.5f * Mathf.Deg2Rad), 0.0f, 1.0f);
                    var sinSpotOuterHalfAngle = Mathf.Sqrt(1.0f - cosSpotOuterHalfAngle * cosSpotOuterHalfAngle);
                    var cosSpotInnerHalfAngle = Mathf.Clamp(Mathf.Cos(spotAngle * 0.5f * innerConePercent * Mathf.Deg2Rad), 0.0f, 1.0f); // inner cone

                    var val = Mathf.Max(0.0001f, (cosSpotInnerHalfAngle - cosSpotOuterHalfAngle));
                    lightData.angleScale = 1.0f / val;
                    lightData.angleOffset = -cosSpotOuterHalfAngle * lightData.angleScale;
                    lightData.iesCut = spotIESCutoffPercent[dataIndex] / 100.0f;

                    // Rescale for cookies and windowing.
                    float cotOuterHalfAngle = cosSpotOuterHalfAngle / sinSpotOuterHalfAngle;
                    lightData.up *= cotOuterHalfAngle;
                    lightData.right *= cotOuterHalfAngle;
                }
                else
                {
                    // These are the neutral values allowing GetAngleAnttenuation in shader code to return 1.0
                    lightData.angleScale = 0.0f;
                    lightData.angleOffset = 1.0f;
                    lightData.iesCut = 1.0f;
                }

                if (lightData.lightType != GPULightType.Directional && lightData.lightType != GPULightType.ProjectorBox)
                {
                    // Store the squared radius of the light to simulate a fill light.
                    float shapeRadiusVal = shapeRadius[dataIndex];
                    lightData.size = new Vector4(shapeRadiusVal * shapeRadiusVal, 0, 0, 0);
                }

                if (lightData.lightType == GPULightType.Rectangle || lightData.lightType == GPULightType.Tube)
                {
                    lightData.size = new Vector4(shapeWidthVal, shapeHeightVal, Mathf.Cos(barnDoorAngle[dataIndex] * Mathf.PI / 180.0f), barnDoorLength[dataIndex]);
                }

/*
                var lightDistanceFade = processedLightEntity.lightDistanceFade;
                var lightVolumetricDistanceFade = processedLightEntity.lightVolumetricDistanceFade;
                var lightDimmer = lightEntities.lightDimmer[processedLightEntity.dataIndex];
                var volumetricDimmer = lightEntities.volumetricDimmer[processedLightEntity.dataIndex];
                lightData.lightDimmer = lightDistanceFade * lightDimmer;
                lightData.diffuseDimmer = lightDistanceFade * (lightEntities.affectDiffuse[processedLightEntity.dataIndex] ? lightDimmer : 0);
                lightData.specularDimmer = lightDistanceFade * (lightEntities.affectSpecular[processedLightEntity.dataIndex] ? lightDimmer * hdCamera.frameSettings.specularGlobalDimmer : 0);
                lightData.volumetricLightDimmer = Mathf.Min(lightVolumetricDistanceFade, lightDistanceFade) * volumetricDimmer;

                lightData.cookieMode = CookieMode.None;
                lightData.shadowIndex = -1;
                lightData.screenSpaceShadowIndex = (int)LightDefinitions.s_InvalidScreenSpaceShadow;
                lightData.isRayTracedContactShadow = 0.0f;

                if (lightComponent != null && additionalLightData != null &&
                    (
                        (lightType == HDLightType.Spot && (lightComponent.cookie != null || additionalLightData.IESPoint != null)) ||
                        ((lightType == HDLightType.Area && lightData.lightType == GPULightType.Rectangle) && (lightComponent.cookie != null || additionalLightData.IESSpot != null)) ||
                        (lightType == HDLightType.Point && (lightComponent.cookie != null || additionalLightData.IESPoint != null))
                    )
                )
                {
                    switch (lightType)
                    {
                        case HDLightType.Spot:
                            lightData.cookieMode = (lightComponent.cookie?.wrapMode == TextureWrapMode.Repeat) ? CookieMode.Repeat : CookieMode.Clamp;
                            if (additionalLightData.IESSpot != null && lightComponent.cookie != null && additionalLightData.IESSpot != lightComponent.cookie)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.Fetch2DCookie(cmd, lightComponent.cookie, additionalLightData.IESSpot);
                            else if (lightComponent.cookie != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.Fetch2DCookie(cmd, lightComponent.cookie);
                            else if (additionalLightData.IESSpot != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.Fetch2DCookie(cmd, additionalLightData.IESSpot);
                            else
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.Fetch2DCookie(cmd, Texture2D.whiteTexture);
                            break;
                        case HDLightType.Point:
                            lightData.cookieMode = CookieMode.Repeat;
                            if (additionalLightData.IESPoint != null && lightComponent.cookie != null && additionalLightData.IESPoint != lightComponent.cookie)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchCubeCookie(cmd, lightComponent.cookie, additionalLightData.IESPoint);
                            else if (lightComponent.cookie != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchCubeCookie(cmd, lightComponent.cookie);
                            else if (additionalLightData.IESPoint != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchCubeCookie(cmd, additionalLightData.IESPoint);
                            break;
                        case HDLightType.Area:
                            lightData.cookieMode = CookieMode.Clamp;
                            if (additionalLightData.areaLightCookie != null && additionalLightData.IESSpot != null && additionalLightData.areaLightCookie != additionalLightData.IESSpot)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.areaLightCookie, additionalLightData.IESSpot);
                            else if (additionalLightData.IESSpot != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.IESSpot);
                            else if (additionalLightData.areaLightCookie != null)
                                lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.areaLightCookie);
                            break;
                    }
                }
                else if (lightType == HDLightType.Spot && additionalLightData.spotLightShape != SpotLightShape.Cone)
                {
                    // Projectors lights must always have a cookie texture.
                    // As long as the cache is a texture array and not an atlas, the 4x4 white texture will be rescaled to 128
                    lightData.cookieMode = CookieMode.Clamp;
                    lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.Fetch2DCookie(cmd, Texture2D.whiteTexture);
                }
                else if (lightData.lightType == GPULightType.Rectangle)
                {
                    if (additionalLightData.areaLightCookie != null || additionalLightData.IESPoint != null)
                    {
                        lightData.cookieMode = CookieMode.Clamp;
                        if (additionalLightData.areaLightCookie != null && additionalLightData.IESSpot != null && additionalLightData.areaLightCookie != additionalLightData.IESSpot)
                            lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.areaLightCookie, additionalLightData.IESSpot);
                        else if (additionalLightData.IESSpot != null)
                            lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.IESSpot);
                        else if (additionalLightData.areaLightCookie != null)
                            lightData.cookieScaleOffset = m_TextureCaches.lightCookieManager.FetchAreaCookie(cmd, additionalLightData.areaLightCookie);
                    }
                }

                var distanceToCamera = processedLightEntity.distanceToCamera;
                var lightsShadowFadeDistance = lightEntities.shadowFadeDistance[processedLightEntity.dataIndex];
                var shadowDimmer = lightEntities.shadowDimmer[processedLightEntity.dataIndex];
                var volumetricShadowDimmer = lightEntities.volumetricShadowDimmer[processedLightEntity.dataIndex];
                float shadowDistanceFade = HDUtils.ComputeLinearDistanceFade(distanceToCamera, Mathf.Min(shadowSettings.maxShadowDistance.value, lightsShadowFadeDistance));
                lightData.shadowDimmer = shadowDistanceFade * shadowDimmer;
                lightData.volumetricShadowDimmer = shadowDistanceFade * volumetricShadowDimmer;
                GetContactShadowMask(additionalLightData, contactShadowsScalableSetting, hdCamera, isRasterization: isRasterization, ref lightData.contactShadowMask, ref lightData.isRayTracedContactShadow);

                // We want to have a colored penumbra if the flag is on and the color is not gray
                bool penumbraTint = additionalLightData.penumbraTint && ((additionalLightData.shadowTint.r != additionalLightData.shadowTint.g) || (additionalLightData.shadowTint.g != additionalLightData.shadowTint.b));
                lightData.penumbraTint = penumbraTint ? 1.0f : 0.0f;
                if (penumbraTint)
                    lightData.shadowTint = new Vector3(Mathf.Pow(additionalLightData.shadowTint.r, 2.2f), Mathf.Pow(additionalLightData.shadowTint.g, 2.2f), Mathf.Pow(additionalLightData.shadowTint.b, 2.2f));
                else
                    lightData.shadowTint = new Vector3(additionalLightData.shadowTint.r, additionalLightData.shadowTint.g, additionalLightData.shadowTint.b);


                var shadowFlags = processedLightEntity.shadowMapFlags;
                // If there is still a free slot in the screen space shadow array and this needs to render a screen space shadow
                if (hdCamera.frameSettings.IsEnabled(FrameSettingsField.RayTracing)
                    && EnoughScreenSpaceShadowSlots(lightData.lightType, screenSpaceChannelSlot)
                    && (shadowFlags & HDVisibleLightEntities.ShadowMapFlags.WillRenderScreenSpaceShadow) != 0
                    && isRasterization)
                {
                    if (lightData.lightType == GPULightType.Rectangle)
                    {
                        // Rectangle area lights require 2 consecutive slots.
                        // Meaning if (screenSpaceChannelSlot % 4 ==3), we'll need to skip a slot
                        // so that the area shadow gets the first two slots of the next following texture
                        if (screenSpaceChannelSlot % 4 == 3)
                        {
                            screenSpaceChannelSlot++;
                        }
                    }

                    // Bind the next available slot to the light
                    lightData.screenSpaceShadowIndex = screenSpaceChannelSlot;

                    // Keep track of the screen space shadow data
                    m_CurrentScreenSpaceShadowData[screenSpaceShadowIndex].additionalLightData = additionalLightData;
                    m_CurrentScreenSpaceShadowData[screenSpaceShadowIndex].lightDataIndex = m_lightList.lights.Count;
                    m_CurrentScreenSpaceShadowData[screenSpaceShadowIndex].valid = true;
                    m_ScreenSpaceShadowsUnion.Add(additionalLightData);

                    // increment the number of screen space shadows
                    screenSpaceShadowIndex++;

                    // Based on the light type, increment the slot usage
                    if (lightData.lightType == GPULightType.Rectangle)
                        screenSpaceChannelSlot += 2;
                    else
                        screenSpaceChannelSlot++;
                }

                lightData.shadowIndex = shadowIndex;

                if (isRasterization)
                {
                    // Keep track of the shadow map (for indirect lighting and transparents)
                    additionalLightData.shadowIndex = shadowIndex;
                }


                //Value of max smoothness is derived from Radius. Formula results from eyeballing. Radius of 0 results in 1 and radius of 2.5 results in 0.
                float maxSmoothness = Mathf.Clamp01(1.1725f / (1.01f + Mathf.Pow(1.0f * (additionalLightData.shapeRadius + 0.1f), 2f)) - 0.15f);
                // Value of max smoothness is from artists point of view, need to convert from perceptual smoothness to roughness
                lightData.minRoughness = (1.0f - maxSmoothness) * (1.0f - maxSmoothness);

                lightData.shadowMaskSelector = Vector4.zero;

                if (processedLightEntity.isBakedShadowMask)
                {
                    lightData.shadowMaskSelector[lightComponent.bakingOutput.occlusionMaskChannel] = 1.0f;
                    lightData.nonLightMappedOnly = lightComponent.lightShadowCasterMode == LightShadowCasterMode.NonLightmappedOnly ? 1 : 0;
                }
                else
                {
                    // use -1 to say that we don't use shadow mask
                    lightData.shadowMaskSelector.x = -1.0f;
                    lightData.nonLightMappedOnly = 0;
                }
*/
                lights[index] = lightData;
                lightDimensionData[index] = lightDimensions;
            }
        }

        public void StartCreateGpuLightDataJob(
            HDCamera hdCamera,
            in CullingResults cullingResult,
            HDVisibleLightEntities visibleLights,
            HDLightEntityCollection lightEntities)
        {
            var createGpuLightDataJob = new CreateGpuLightDataJob()
            {
                //Parameters
                lightLayersEnabled = hdCamera.frameSettings.IsEnabled(FrameSettingsField.LightLayers),

                // light entity SoA data
                lightLayers = lightEntities.lightLayers,
                lightDimmer = lightEntities.lightDimmer,
                volumetricDimmer = lightEntities.volumetricDimmer,
                affectDiffuse = lightEntities.affectDiffuse,
                affectSpecular = lightEntities.affectSpecular,
                applyRangeAttenuation = lightEntities.applyRangeAttenuation,
                shadowFadeDistance = lightEntities.shadowFadeDistance,
                shapeWidth = lightEntities.shapeWidth,
                shapeHeight = lightEntities.shapeHeight,
                aspectRatio = lightEntities.aspectRatio,
                innerSpotPercent = lightEntities.innerSpotPercent,
                spotIESCutoffPercent = lightEntities.spotIESCutoffPercent,
                shapeRadius = lightEntities.shapeRadius,
                barnDoorLength = lightEntities.barnDoorLength,
                barnDoorAngle = lightEntities.barnDoorAngle,
                shadowTint = lightEntities.shadowTint,

                //visible lights processed
                sortKeys = visibleLights.sortKeys,
                processedEntities = visibleLights.processedEntities,
                visibleLights = cullingResult.visibleLights,

                //outputs
                lights = m_Lights,
                lightDimensionData = m_LightDimensions
            };

            createGpuLightDataJob.Schedule(visibleLights.preprocessedLightCounts, 32);
        }

        public void CompleteGpuLightDataJob()
        {
            m_CreateGpuLightDataJobHandle.Complete();
        }
    }
}
