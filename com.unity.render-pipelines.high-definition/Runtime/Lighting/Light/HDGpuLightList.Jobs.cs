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
            #endregion

            #region Light entity SoA data
            #endregion

            #region output processed lights
            [WriteOnly]
            NativeArray<LightData> lights;
            [WriteOnly]
            NativeArray<DirectionalLightData> directionalLights;
            #endregion

            public void Execute(int index)
            {
            }
        }

        public void StartCreateGpuLightDataJob(HDVisibleLightEntities visibleLights, HDLightEntityCollection lightEntities)
        {
            var createGpuLightDataJob = new CreateGpuLightDataJob()
            {
            };

            createGpuLightDataJob.Schedule(visibleLights.preprocessedLightCounts, 32);
        }

        public void CompleteGpuLightDataJob()
        {
            m_CreateGpuLightDataJobHandle.Complete();
        }
    }
}
