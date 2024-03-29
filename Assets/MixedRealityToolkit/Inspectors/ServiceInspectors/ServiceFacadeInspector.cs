﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.﻿

using Microsoft.MixedReality.Toolkit.Boundary;
using Microsoft.MixedReality.Toolkit.CameraSystem;
using Microsoft.MixedReality.Toolkit.Diagnostics;
using Microsoft.MixedReality.Toolkit.Editor;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.SceneSystem;
using Microsoft.MixedReality.Toolkit.SpatialAwareness;
using Microsoft.MixedReality.Toolkit.Utilities.Editor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.Utilities.Facades
{
    [CustomEditor(typeof(ServiceFacade))]
    [InitializeOnLoad]
    public class ServiceFacadeEditor : UnityEditor.Editor
    {
        static ServiceFacadeEditor()
        {
            // Register this on startup so we can update whether a facade inspector is updated or not
#if UNITY_2019_1_OR_NEWER
            SceneView.duringSceneGui += DrawSceneGUI;
#else
            SceneView.onSceneGUIDelegate += DrawSceneGUI;
#endif
        }

        private static List<string> dataProviderList = new List<string>();
        private static Dictionary<Type, Type> inspectorTypeLookup = new Dictionary<Type, Type>();
        private static Dictionary<Type, IMixedRealityServiceInspector> inspectorInstanceLookup = new Dictionary<Type, IMixedRealityServiceInspector>();
        private static bool initializedServiceInspectorLookup = false;

        Color proHeaderColor = (Color)new Color32(56, 56, 56, 255);
        Color defaultHeaderColor = (Color)new Color32(194, 194, 194, 255);

        const int headerXOffset = 48;

        [SerializeField]
        private Texture2D logoLightTheme = null;

        [SerializeField]
        private Texture2D logoDarkTheme = null;

        protected virtual void Awake()
        {
            string assetPath = "StandardAssets/Textures";

            if (logoLightTheme == null)
            {
                logoLightTheme = (Texture2D)AssetDatabase.LoadAssetAtPath(MixedRealityToolkitFiles.MapRelativeFilePath($"{assetPath}/MRTK_Logo_Black.png"), typeof(Texture2D));
            }

            if (logoDarkTheme == null)
            {
                logoDarkTheme = (Texture2D)AssetDatabase.LoadAssetAtPath(MixedRealityToolkitFiles.MapRelativeFilePath($"{assetPath}/MRTK_Logo_White.png"), typeof(Texture2D));
            }
        }

        protected override void OnHeaderGUI()
        {
            ServiceFacade facade = (ServiceFacade)target;
            
            // Draw a rect over the top of the existing header label
            var labelRect = EditorGUILayout.GetControlRect(false, 0f);
            labelRect.height = EditorGUIUtility.singleLineHeight;
            labelRect.y -= labelRect.height;
            labelRect.x = headerXOffset;
            labelRect.xMax -= labelRect.x * 2f;

            EditorGUI.DrawRect(labelRect, EditorGUIUtility.isProSkin ? proHeaderColor : defaultHeaderColor);

            // Draw a new label
            string header = facade.name;
            if (string.IsNullOrEmpty(header))
                header = target.ToString();

            EditorGUI.LabelField(labelRect, header, EditorStyles.boldLabel);
        }

        public override void OnInspectorGUI()
        {
            OnHeaderGUI();

            ServiceFacade facade = (ServiceFacade)target;

            if (facade.Service == null)
            {   // Facade has likely been destroyed
                return;
            }

            if (!MixedRealityToolkit.IsInitialized || !MixedRealityToolkit.Instance.HasActiveProfile)
            {   
                return;
            }

            InitializeServiceInspectorLookup();

            bool drawDataProviders = DrawDataProviders(facade.ServiceType);
            bool drawProfile = DrawProfile(facade.ServiceType);
            bool drawInspector = DrawInspector(facade);

            // Only draw the doc link if we didn't draw a profile
            // Profiles include doc links by default now
            if (!drawProfile)
            {
                DrawDocLink(facade.ServiceType);
            }

            bool drewSomething = drawProfile | drawInspector | drawDataProviders;

            if (!drewSomething)
            {
                // If we haven't drawn anything at all, draw a label so people aren't confused.
                EditorGUILayout.HelpBox("No inspector has been defined for this service type.", MessageType.Info);
            }

        }

        /// <summary>
        /// Draws button linking to documentation.
        /// </summary>
        /// <param name="serviceType">type of service to target</param>
        /// <returns>true if doc link is found, false otherwise</returns>
        private bool DrawDocLink(Type serviceType)
        {
            DocLinkAttribute docLink = serviceType.GetCustomAttribute<DocLinkAttribute>();
            if (docLink != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                InspectorUIUtility.RenderDocLinkButton(docLink.URL);

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                EditorGUILayout.Space();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Draws a list of services that use this as a data provider
        /// </summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        private bool DrawDataProviders(Type serviceType)
        {  
            // If this is a data provider being used by other services, mention that now
            dataProviderList.Clear();
            foreach (MixedRealityDataProviderAttribute dataProviderAttribute in serviceType.GetCustomAttributes(typeof(MixedRealityDataProviderAttribute), true))
            {
                dataProviderList.Add(" • " + dataProviderAttribute.ServiceInterfaceType.Name);
            }

            if (dataProviderList.Count > 0)
            {
                EditorGUILayout.HelpBox("This data provider is used by the following services:\n " + String.Join("\n", dataProviderList.ToArray()), MessageType.Info);
                EditorGUILayout.Space();
                return true;
            }
            return false;
        }
               
        /// <summary>
        /// Draws the custom inspector gui for service type.
        /// </summary>
        /// <param name="facade"></param>
        /// <returns></returns>
        private bool DrawInspector(ServiceFacade facade)
        {
            IMixedRealityServiceInspector inspectorInstance;
            if (GetServiceInspectorInstance(facade.ServiceType, out inspectorInstance))
            {
                inspectorInstance.DrawInspectorGUI(facade.Service);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Draws the profile for the service type, if wanted by inspector and found.
        /// </summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        private bool DrawProfile(Type serviceType)
        {
            IMixedRealityServiceInspector inspectorInstance;
            if (GetServiceInspectorInstance(serviceType, out inspectorInstance))
            {
                if (!inspectorInstance.DrawProfileField)
                {   // We've been instructed to skip drawing a profile by the inspector
                    return false;
                }
            }

            bool foundAndDrewProfile = false;

            // Draw the base profile stuff
            if (typeof(BaseCoreSystem).IsAssignableFrom(serviceType))
            {
                SerializedObject activeProfileObject = new SerializedObject(MixedRealityToolkit.Instance.ActiveProfile);
                // Would be nice to handle this using some other method
                // Would be nice to handle this with a lookup instead
                if (typeof(IMixedRealityInputSystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("inputSystemProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
                else if (typeof(IMixedRealityBoundarySystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("boundaryVisualizationProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
                else if (typeof(IMixedRealityDiagnosticsSystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("diagnosticsSystemProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
                else if (typeof(IMixedRealitySpatialAwarenessSystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("spatialAwarenessSystemProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
                else if (typeof(IMixedRealityCameraSystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("cameraProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
                else if (typeof(IMixedRealitySceneSystem).IsAssignableFrom(serviceType))
                {
                    SerializedProperty serviceProfileProp = activeProfileObject.FindProperty("sceneSystemProfile");
                    BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                    EditorGUILayout.Space();
                    foundAndDrewProfile = true;
                }
            }
            else if (typeof(BaseExtensionService).IsAssignableFrom(serviceType))
            {
                // Make sure the extension service profile isn't null
                if (MixedRealityToolkit.Instance.ActiveProfile.RegisteredServiceProvidersProfile != null)
                {
                    // If this is an extension service, see if it uses a profile
                    MixedRealityServiceConfiguration[] serviceConfigs = MixedRealityToolkit.Instance.ActiveProfile.RegisteredServiceProvidersProfile.Configurations;
                    for (int serviceIndex = 0; serviceIndex < serviceConfigs.Length; serviceIndex++)
                    {
                        MixedRealityServiceConfiguration serviceConfig = serviceConfigs[serviceIndex];
                        if (serviceConfig.ComponentType.Type.IsAssignableFrom(serviceType) && serviceConfig.ConfigurationProfile != null)
                        {
                            // We found the service that this type uses - draw the profile
                            SerializedObject serviceConfigObject = new SerializedObject(MixedRealityToolkit.Instance.ActiveProfile.RegisteredServiceProvidersProfile);
                            SerializedProperty serviceConfigArray = serviceConfigObject.FindProperty("configurations");
                            SerializedProperty serviceConfigProp = serviceConfigArray.GetArrayElementAtIndex(serviceIndex);
                            SerializedProperty serviceProfileProp = serviceConfigProp.FindPropertyRelative("configurationProfile");
                            BaseMixedRealityProfileInspector.RenderReadOnlyProfile(serviceProfileProp);
                            EditorGUILayout.Space();
                            foundAndDrewProfile = true;
                            break;
                        }
                    }
                }
            }

            return foundAndDrewProfile;
        }

        /// <summary>
        /// Gathers service types from all assemblies.
        /// </summary>
        private static void InitializeServiceInspectorLookup()
        {
            if (initializedServiceInspectorLookup)
            {
                return;
            }

            inspectorTypeLookup.Clear();

            var typesWithMyAttribute =
                 from assembly in AppDomain.CurrentDomain.GetAssemblies().AsParallel()
                 from classType in assembly.GetTypes()
                 let attribute = classType.GetCustomAttribute<MixedRealityServiceInspectorAttribute>(true)
                 where attribute != null
                 select new { ClassType = classType, Attribute = attribute };

            foreach (var result in typesWithMyAttribute)
            {
                inspectorTypeLookup.Add(result.Attribute.ServiceType, result.ClassType);
            }

            initializedServiceInspectorLookup = true;
        }

        /// <summary>
        /// Draws gizmos for facade.
        /// </summary>
        /// <param name="facade"></param>
        /// <param name="type"></param>
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active)]
        private static void DrawGizmos(ServiceFacade facade, GizmoType type)
        {
            if (facade.Service == null)
            {
                return;
            }

            if (!MixedRealityToolkit.IsInitialized || !MixedRealityToolkit.Instance.HasActiveProfile)
            {
                return;
            }

            InitializeServiceInspectorLookup();

            // Find and draw the custom inspector
            IMixedRealityServiceInspector inspectorInstance;
            if (!GetServiceInspectorInstance(facade.ServiceType, out inspectorInstance))
            {
                return;
            }

            // If we've implemented a facade inspector, draw gizmos now
            inspectorInstance.DrawGizmos(facade.Service);
        }

        /// <summary>
        /// Draws scene gui for facade.
        /// </summary>
        /// <param name="sceneView"></param>
        private static void DrawSceneGUI(SceneView sceneView)
        {
            if (!MixedRealityToolkit.IsInitialized || !MixedRealityToolkit.Instance.HasActiveProfile)
            {
                return;
            }

            InitializeServiceInspectorLookup();

            foreach (KeyValuePair<Type, Type> inspectorTypePair in inspectorTypeLookup)
            {
                // Find the facade associated with this service
                ServiceFacade facade;
                // If no facade exists for this service type, move on
                if (!ServiceFacade.FacadeServiceLookup.TryGetValue(inspectorTypePair.Key, out facade) || facade.Destroyed || facade == null)
                {
                    continue;
                }

                IMixedRealityServiceInspector inspectorInstance;
                if (!GetServiceInspectorInstance(inspectorTypePair.Key, out inspectorInstance))
                {
                    continue;
                }

                if (Selection.Contains(facade) || inspectorInstance.AlwaysDrawSceneGUI)
                {
                    inspectorInstance.DrawSceneGUI(facade.Service, sceneView);
                }
            }
        }

        /// <summary>
        /// Gets an instance of the service type. Returns false if no instance is found.
        /// </summary>
        /// <param name="serviceType"></param>
        /// <param name="inspectorInstance"></param>
        /// <returns></returns>
        private static bool GetServiceInspectorInstance(Type serviceType, out IMixedRealityServiceInspector inspectorInstance)
        {
            inspectorInstance = null;

            Type inspectorType;
            if (inspectorTypeLookup.TryGetValue(serviceType, out inspectorType))
            {
                if (!inspectorInstanceLookup.TryGetValue(inspectorType, out inspectorInstance))
                {
                    // If an instance of the class doesn't exist yet, create it now
                    try
                    {
                        inspectorInstance = (IMixedRealityServiceInspector)Activator.CreateInstance(inspectorType);
                        inspectorInstanceLookup.Add(inspectorType, inspectorInstance);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("Couldn't create instance of inspector type " + inspectorType);
                        Debug.LogException(e);
                    }
                }
                else
                {
                    return true;
                }
            }

            return inspectorInstance != null;
        }
    }
}