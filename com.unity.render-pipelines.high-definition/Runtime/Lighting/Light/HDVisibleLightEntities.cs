using System;
using Unity.Collections;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityEngine.Rendering.HighDefinition
{
    //Class representing lights in the context of a view.
    internal partial class HDVisibleLightEntities : IDisposable
    {
        static private HashSet<HDVisibleLightEntities> s_Instances = new HashSet<HDVisibleLightEntities>();

        public const int ArrayCapacity = 32;
        int m_Capacity = 0;
        int m_Size = 0;

        internal enum ProcessLightsCountSlots
        {
            ProcessedLights,
            DirectionalLights,
            PunctualLights,
            AreaLightCounts,
            ShadowLights,
        }

        [Flags]
        internal enum ShadowMapFlags
        {
            None = 0,
            WillRenderShadowMap = 1 << 0,
            WillRenderScreenSpaceShadow = 1 << 1,
            WillRenderRayTracedShadow = 1 << 2
        }

        internal struct ProcessedVisibleLightEntity
        {
            public int dataIndex;
            public GPULightType gpuLightType;
            public HDLightType lightType;
            public float lightDistanceFade;
            public float lightVolumetricDistanceFade;
            public float distanceToCamera;
            public HDVisibleLightEntities.ShadowMapFlags shadowMapFlags;
            public bool isBakedShadowMask;
        }

        NativeArray<int> m_ProcessVisibleLightCounts;

        #region visible lights SoA
        NativeArray<HDLightEntityData> m_VisibleEntities;
        NativeArray<LightBakingOutput> m_VisibleLightBakingOutput;
        NativeArray<LightShadows>      m_VisibleLightShadows;
        NativeArray<LightVolumeType>   m_ProcessedLightVolumeType;
        NativeArray<ProcessedVisibleLightEntity> m_ProcessedEntities;
        #endregion

        #region ProcessedLight data SoA
        NativeArray<uint>            m_SortKeys;
        NativeArray<uint>            m_SortSupportArray;
        NativeArray<int>             m_ShadowLightsDataIndices;

        public int preprocessedLightCounts => m_ProcessVisibleLightCounts.IsCreated ? m_ProcessVisibleLightCounts[(int)ProcessLightsCountSlots.ProcessedLights] : 0;

        public NativeArray<HDLightEntityData> visibleEntities => m_VisibleEntities;
        public NativeArray<LightVolumeType> processedLightVolumeType => m_ProcessedLightVolumeType;
        public NativeArray<ProcessedVisibleLightEntity> processedEntities => m_ProcessedEntities;
        public NativeArray<uint>            sortKeys => m_SortKeys;
        public NativeArray<uint>            sortSupportArray => m_SortSupportArray;
        public NativeArray<int>             shadowLightsDataIndices => m_ShadowLightsDataIndices;

        private void ResizeArrays(int newCapacity)
        {
            m_Capacity = Math.Max(Math.Max(newCapacity, ArrayCapacity), m_Capacity * 2);
            m_VisibleEntities.ResizeArray(m_Capacity);
            m_VisibleLightBakingOutput.ResizeArray(m_Capacity);
            m_VisibleLightShadows.ResizeArray(m_Capacity);

            m_ProcessedLightVolumeType.ResizeArray(m_Capacity);
            m_ProcessedEntities.ResizeArray(m_Capacity);
            m_SortKeys.ResizeArray(m_Capacity);
            m_ShadowLightsDataIndices.ResizeArray(m_Capacity);
        }

        private void DisposeArrays()
        {
            if (m_SortSupportArray.IsCreated)
                m_SortSupportArray.Dispose();

            if (m_Capacity == 0)
                return;

            m_ProcessVisibleLightCounts.Dispose();

            m_VisibleEntities.Dispose();
            m_VisibleLightBakingOutput.Dispose();
            m_VisibleLightShadows.Dispose();

            m_ProcessedLightVolumeType.Dispose();
            m_ProcessedEntities.Dispose();
            m_SortKeys.Dispose();
            m_ShadowLightsDataIndices.Dispose();

            m_Capacity = 0;
            m_Size = 0;
        }

        ~HDVisibleLightEntities()
        {
            Dispose();
        }

        #endregion

        public static HDVisibleLightEntities Get()
        {
            var instance = UnsafeGenericPool<HDVisibleLightEntities>.Get();
            s_Instances.Add(instance);
            return instance;
        }

        public static void Release(HDVisibleLightEntities obj)
        {
            UnsafeGenericPool<HDVisibleLightEntities>.Release(obj);
        }

        private void SortLightKeys()
        {
            using (new ProfilingScope(null, ProfilingSampler.Get(HDProfileId.SortVisibleLights)))
            {
                //Tunning against ps4 console,
                //32 items insertion sort has a workst case of 3 micro seconds.
                //200 non recursive merge sort has around 23 micro seconds.
                //From 200 and more, Radix sort beats everything.
                var sortSize = preprocessedLightCounts;
                if (sortSize <= 32)
                    CoreUnsafeUtils.InsertionSort(m_SortKeys, sortSize);
                else if (m_Size <= 200)
                    CoreUnsafeUtils.MergeSort(m_SortKeys, sortSize, ref m_SortSupportArray);
                else
                    CoreUnsafeUtils.RadixSort(m_SortKeys, sortSize, ref m_SortSupportArray);
            }
        }

        public void PreprocessVisibleLights(
            HDCamera hdCamera,
            in CullingResults cullingResult,
            HDShadowManager shadowManager,
            in HDShadowInitParameters inShadowInitParameters,
            in AOVRequestData aovRequestData,
            in GlobalLightLoopSettings lightLoopSettings,
            DebugDisplaySettings debugDisplaySettings)
        {
            BuildVisibleLightEntities(cullingResult);

            if (m_Size == 0)
                return;

            FilterVisibleLightsByAOV(aovRequestData);
            StartProcessVisibleLightJob(hdCamera, cullingResult.visibleLights, lightLoopSettings, debugDisplaySettings);
            CompleteProcessVisibleLightJob();
            SortLightKeys();
            ProcessShadows(hdCamera, shadowManager, inShadowInitParameters, cullingResult);
        }

        public static void Cleanup()
        {
            foreach (var obj in s_Instances)
            {
                obj.Dispose();
            }
            s_Instances.Clear();
        }

        public void Dispose()
        {
            DisposeArrays();
        }

        public void Reset()
        {
            m_Size = 0;
            //Track object if its recycled. This avoids memory leaks between pipeline sessions (during cleanup all the buffers can get freed).
            if (!s_Instances.Contains(this))
                s_Instances.Add(this);
        }

        #region Internal implementation

        private void BuildVisibleLightEntities(in CullingResults cullResults)
        {
            m_Size = 0;
            HDLightEntityCollection.instance.CompleteLightTransformDataJobs();
            using (new ProfilingScope(null, ProfilingSampler.Get(HDProfileId.BuildVisibleLightEntities)))
            {
                if (cullResults.visibleLights.Length == 0
                    || HDLightEntityCollection.instance == null)
                    return;

                if (cullResults.visibleLights.Length > m_Capacity)
                {
                    ResizeArrays(cullResults.visibleLights.Length);
                }

                m_Size = cullResults.visibleLights.Length;

                //TODO: this should be accelerated by a c++ API
                for (int i = 0; i < cullResults.visibleLights.Length; ++i)
                {
                    Light light = cullResults.visibleLights[i].light;
                    var entityData = HDLightEntityCollection.instance.FindEntity(light);
                    if (!entityData.valid)
                    {
                        var defaultEntity = HDLightEntityCollection.instance.GetDefaultLightEntity();
                        entityData = HDLightEntityCollection.instance.GetEntityData(defaultEntity);
                    }

                    m_VisibleEntities[i] = entityData;
                    m_VisibleLightBakingOutput[i] = light.bakingOutput;
                    m_VisibleLightShadows[i] = light.shadows;
                }
            }
        }

        private void ProcessShadows(
            HDCamera hdCamera,
            HDShadowManager shadowManager,
            in HDShadowInitParameters inShadowInitParameters,
            in CullingResults cullResults)
        {
            int shadowLights = m_ProcessVisibleLightCounts[(int)ProcessLightsCountSlots.ShadowLights];
            if (shadowLights == 0)
                return;

            using (new ProfilingScope(null, ProfilingSampler.Get(HDProfileId.ProcessShadows)))
            {
                NativeArray<VisibleLight> visibleLights = cullResults.visibleLights;
                var hdShadowSettings = hdCamera.volumeStack.GetComponent<HDShadowSettings>();

                unsafe 
                {
                    ProcessedVisibleLightEntity* entitiesPtr = (ProcessedVisibleLightEntity*)m_ProcessedEntities.GetUnsafePtr<ProcessedVisibleLightEntity>();
                    for (int i = 0; i < shadowLights; ++i)
                    {
                        int lightIndex = m_ShadowLightsDataIndices[i];
                        ProcessedVisibleLightEntity* entity = entitiesPtr + lightIndex;
                        if (!cullResults.GetShadowCasterBounds(lightIndex, out var bounds))
                        {
                            entity->shadowMapFlags = ShadowMapFlags.None;
                            continue;
                        }

                        HDAdditionalLightData additionalLightData = HDLightEntityCollection.instance.hdAdditionalLightData[entity->dataIndex];
                        if (additionalLightData == null)
                            continue;

                        VisibleLight visibleLight = visibleLights[lightIndex];
                        additionalLightData.ReserveShadowMap(hdCamera.camera, shadowManager, hdShadowSettings, inShadowInitParameters, visibleLight, entity->lightType);
                    }
                }
            }
        }

        private void FilterVisibleLightsByAOV(AOVRequestData aovRequest)
        {
            if (!aovRequest.hasLightFilter)
                return;

            for (int i = 0; i < m_Size; ++i)
            {
                var visibleEntity = m_VisibleEntities[i];
                var go = HDLightEntityCollection.instance.aovGameObjects[visibleEntity.dataIndex];
                if (go == null)
                    continue;

                if (!aovRequest.IsLightEnabled(go))
                    m_VisibleEntities[i] = HDLightEntityData.Invalid;
            }
        }

        #endregion
    }
}
