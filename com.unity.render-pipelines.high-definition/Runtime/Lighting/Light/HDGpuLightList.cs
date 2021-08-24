using System;
using UnityEngine.Jobs;
using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering.HighDefinition
{
    internal partial class HDGpuLightList
    {
        private NativeArray<LightData> m_Lights;
        private int m_LightCapacity = 0;
        private int m_LightCount = 0;

        private NativeArray<DirectionalLightData> m_DirectionalLights;
        private int m_DirectionalLightCapacity = 0;
        private int m_DirectionalLightCount = 0;

        public const int ArrayCapacity = 100;
        NativeArray<LightData> lights => m_Lights;
        NativeArray<DirectionalLightData> directionalLights => m_DirectionalLights;

        private void Allocate(int lightCount, int directionalLightCount)
        {
            if (lightCount > m_LightCapacity)
            {
                m_LightCapacity = Math.Max(Math.Max(m_LightCapacity * 2, lightCount), ArrayCapacity);
                m_Lights.ResizeArray(m_LightCapacity);
            }

            if (directionalLightCount > m_DirectionalLightCapacity)
            {
                m_DirectionalLightCapacity = Math.Max(Math.Max(m_DirectionalLightCapacity * 2, directionalLightCount), ArrayCapacity);
                m_DirectionalLights.ResizeArray(m_DirectionalLightCapacity);
            }

            m_LightCount = lightCount;
        }

        public void BuildLightGPUData(HDVisibleLightEntities visibleLights, HDLightEntityCollection lightEntities)
        {
            StartCreateGpuLightDataJob(visibleLights, lightEntities);
            CompleteGpuLightDataJob();
        }

        public void Cleanup()
        {
            if (m_Lights.IsCreated)
                m_Lights.Dispose();

            if (m_DirectionalLights.IsCreated)
                m_DirectionalLights.Dispose();
        }
    }
}
