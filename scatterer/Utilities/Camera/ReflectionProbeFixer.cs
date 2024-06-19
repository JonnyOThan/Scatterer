// Stock KSP reflectionProbes Reflection probes seem to render the scaled scenery and the local scenery together, without clearing the depth buffer in between
// and without adjusting for depth/scale differences between the two. This causes issues: https://bugs.kerbalspaceprogram.com/issues/25179
// Fix this by adding a separate scaledSpace Camera

using UnityEngine;
using System.Collections;
using System.IO;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

using KSP.IO;

namespace Scatterer
{
	public class ReflectionProbeFixer : MonoBehaviour
	{
		Camera scaledCamera;
		GameObject scaledCameraGO;
		
		Camera reflectionProbeCamera;
		int tweakedCullingMask;
		
		public ReflectionProbeFixer ()
		{
		}

		public void Awake()
		{
			// Create a camera that will render scaledSpace for reflection probes
			scaledCameraGO = new GameObject("ScattererReflectionProbeScaledSpaceCamera");
			scaledCamera = scaledCameraGO.AddComponent<Camera>();
			scaledCamera.enabled = false;
			reflectionProbeCamera = gameObject.GetComponent<Camera> ();

			// Remove scaledSpace rendering from the stock reflection probe Camera
			tweakedCullingMask = reflectionProbeCamera.cullingMask;
			if ((tweakedCullingMask & (1 << 10)) != 0)
			{
				tweakedCullingMask = tweakedCullingMask - (1 << 10);
			}
		}

		// We need to do this every frame as it gets reset
		public void OnPreCull()
		{
			reflectionProbeCamera.cullingMask = tweakedCullingMask; 
			reflectionProbeCamera.clearFlags = CameraClearFlags.Depth; // Clear only depth for this Camera, scaledCamera clears color+depth

			scaledCamera.CopyFrom(reflectionProbeCamera);
			scaledCamera.enabled = false;
			scaledCamera.clearFlags = CameraClearFlags.Color;
			scaledCamera.backgroundColor = Color.black;
			
			// Setup and render scaled scene first
			scaledCamera.cullingMask = ScaledCamera.Instance.galaxyCamera.cullingMask;

            // The reflectionProbe camera has manually set viewMatrix and doesn't use the transforms, we want to keep everything the same except for position
            Matrix4x4 viewMatrix = scaledCamera.worldToCameraMatrix;

            viewMatrix.m03 = ScaledCamera.Instance.galaxyCamera.transform.position.x;
            viewMatrix.m13 = ScaledCamera.Instance.galaxyCamera.transform.position.y;
            viewMatrix.m23 = ScaledCamera.Instance.galaxyCamera.transform.position.z;

            scaledCamera.worldToCameraMatrix = viewMatrix;

			scaledCamera.targetTexture = reflectionProbeCamera.targetTexture;
			scaledCamera.Render ();

            // Render scaled scene second
            scaledCamera.clearFlags = CameraClearFlags.Depth;
            scaledCamera.cullingMask = (1<<9) | (1<<10);

            viewMatrix.m03 = Scatterer.Instance.scaledSpaceCamera.transform.position.x;
            viewMatrix.m13 = Scatterer.Instance.scaledSpaceCamera.transform.position.y;
            viewMatrix.m23 = Scatterer.Instance.scaledSpaceCamera.transform.position.z;

            scaledCamera.worldToCameraMatrix = viewMatrix;

            scaledCamera.Render();
        }

        public void OnDestroy()
		{
			if (scaledCamera)
			{
				Component.DestroyImmediate(scaledCamera);
			}

			if (scaledCameraGO)
			{
				UnityEngine.Object.DestroyImmediate (scaledCameraGO);
			}
		}
	}
}

