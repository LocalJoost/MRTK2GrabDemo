﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.Toolkit.Utilities;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.Input
{
    [MixedRealityController(
        SupportedControllerType.GGVHand,
        new[] { Handedness.Left, Handedness.Right })]
    public class SimulatedGestureHand : SimulatedHand
    {
        public override HandSimulationMode SimulationMode => HandSimulationMode.Gestures;

        private bool initializedFromProfile = false;
        private MixedRealityInputAction holdAction = MixedRealityInputAction.None;
        private MixedRealityInputAction navigationAction = MixedRealityInputAction.None;
        private MixedRealityInputAction manipulationAction = MixedRealityInputAction.None;
        private bool useRailsNavigation = false;
        float holdStartDuration = 0.0f;
        float navigationStartThreshold = 0.0f;

        private float SelectDownStartTime = 0.0f;
        private bool holdInProgress = false;
        private bool manipulationInProgress = false;
        private bool navigationInProgress = false;
        private Vector3 currentRailsUsed = Vector3.one;
        private Vector3 currentPosition = Vector3.zero;
        private Vector3 cumulativeDelta = Vector3.zero;
        private MixedRealityPose currentGripPose = MixedRealityPose.ZeroIdentity;

        private Vector3 navigationDelta => new Vector3(
            Mathf.Clamp(cumulativeDelta.x, -1.0f, 1.0f) * currentRailsUsed.x,
            Mathf.Clamp(cumulativeDelta.y, -1.0f, 1.0f) * currentRailsUsed.y,
            Mathf.Clamp(cumulativeDelta.z, -1.0f, 1.0f) * currentRailsUsed.z);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="trackingState"></param>
        /// <param name="controllerHandedness"></param>
        /// <param name="inputSource"></param>
        /// <param name="interactions"></param>
        public SimulatedGestureHand(
            TrackingState trackingState, 
            Handedness controllerHandedness, 
            IMixedRealityInputSource inputSource = null, 
            MixedRealityInteractionMapping[] interactions = null)
                : base(trackingState, controllerHandedness, inputSource, interactions)
        {
        }

        /// Lazy-init settings based on profile.
        /// This cannot happen in the constructor because the profile may not exist yet.
        private void EnsureProfileSettings()
        {
            if (initializedFromProfile)
            {
                return;
            }
            initializedFromProfile = true;

            var gestureProfile = InputSystem?.InputSystemProfile?.GesturesProfile;
            if (gestureProfile != null)
            {
                for (int i = 0; i < gestureProfile.Gestures.Length; i++)
                {
                    var gesture = gestureProfile.Gestures[i];
                    switch (gesture.GestureType)
                    {
                        case GestureInputType.Hold:
                            holdAction = gesture.Action;
                            break;
                        case GestureInputType.Manipulation:
                            manipulationAction = gesture.Action;
                            break;
                        case GestureInputType.Navigation:
                            navigationAction = gesture.Action;
                            break;
                    }
                }

                useRailsNavigation = gestureProfile.UseRailsNavigation;
            }

            var inputSimProfile = MixedRealityToolkit.Instance?.GetService<IInputSimulationService>()?.InputSimulationProfile;
            if (inputSimProfile != null)
            {
                holdStartDuration = inputSimProfile.HoldStartDuration;
                navigationStartThreshold = inputSimProfile.NavigationStartThreshold;
            }
        }

        /// <summary>
        /// The GGV default interactions.
        /// </summary>
        /// <remarks>A single interaction mapping works for both left and right controllers.</remarks>
        public override MixedRealityInteractionMapping[] DefaultInteractions => new[]
        {
            new MixedRealityInteractionMapping(0, "Select", AxisType.Digital, DeviceInputType.Select, MixedRealityInputAction.None),
            new MixedRealityInteractionMapping(1, "Grip Pose", AxisType.SixDof, DeviceInputType.SpatialGrip, MixedRealityInputAction.None),
        };

        public override void SetupDefaultInteractions(Handedness controllerHandedness)
        {
            AssignControllerMappings(DefaultInteractions);
        }

        protected override void UpdateInteractions(SimulatedHandData handData)
        {
            EnsureProfileSettings();

            Vector3 lastPosition = currentPosition;
            currentPosition = jointPoses[TrackedHandJoint.IndexTip].Position;
            cumulativeDelta += currentPosition - lastPosition;
            currentGripPose.Position = currentPosition;

            if (lastPosition != currentPosition)
            {
                InputSystem?.RaiseSourcePositionChanged(InputSource, this, currentPosition);
            }

            for (int i = 0; i < Interactions?.Length; i++)
            {

                switch (Interactions[i].InputType)
                {
                    case DeviceInputType.SpatialGrip:
                        Interactions[i].PoseData = currentGripPose;
                        if (Interactions[i].Changed)
                        {
                            InputSystem?.RaisePoseInputChanged(InputSource, ControllerHandedness, Interactions[i].MixedRealityInputAction, currentGripPose);
                        }
                        break;
                    case DeviceInputType.Select:
                        Interactions[i].BoolData = handData.IsPinching;

                        if (Interactions[i].Changed)
                        {
                            if (Interactions[i].BoolData)
                            {
                                InputSystem?.RaiseOnInputDown(InputSource, ControllerHandedness, Interactions[i].MixedRealityInputAction);

                                SelectDownStartTime = Time.time;
                                cumulativeDelta = Vector3.zero;

                                TryStartManipulation();
                            }
                            else
                            {
                                InputSystem?.RaiseOnInputUp(InputSource, ControllerHandedness, Interactions[i].MixedRealityInputAction);

                                // Stop active gestures
                                TryCompleteHold();
                                TryCompleteManipulation();
                                TryCompleteNavigation();
                            }
                        }
                        else if (Interactions[i].BoolData)
                        {
                            if (manipulationInProgress)
                            {
                                UpdateManipulation();
                            }
                            if (navigationInProgress)
                            {
                                UpdateNavigation();
                            }

                            if (cumulativeDelta.magnitude > navigationStartThreshold)
                            {
                                TryCancelHold();
                                TryStartNavigation();
                            }
                            else if (Time.time >= SelectDownStartTime + holdStartDuration)
                            {
                                TryStartHold();
                            }
                        }
                        break;
                }
            }
        }

        private bool TryStartHold()
        {
            if (!holdInProgress)
            {
                InputSystem?.RaiseGestureStarted(this, holdAction);
                holdInProgress = true;
                return true;
            }
            return false;
        }

        private bool TryCompleteHold()
        {
            if (holdInProgress)
            {
                InputSystem?.RaiseGestureCompleted(this, holdAction);
                holdInProgress = false;
                return true;
            }
            return false;
        }

        private bool TryCancelHold()
        {
            if (holdInProgress)
            {
                InputSystem?.RaiseGestureCanceled(this, holdAction);
                holdInProgress = false;
                return true;
            }
            return false;
        }

        private bool TryStartManipulation()
        {
            if (!manipulationInProgress)
            {
                InputSystem?.RaiseGestureStarted(this, manipulationAction);
                manipulationInProgress = true;
                return true;
            }
            return false;
        }

        private void UpdateManipulation()
        {
            if (manipulationInProgress)
            {
                InputSystem?.RaiseGestureUpdated(this, manipulationAction, cumulativeDelta);
            }
        }

        private bool TryCompleteManipulation()
        {
            if (manipulationInProgress)
            {
                InputSystem?.RaiseGestureCompleted(this, manipulationAction, cumulativeDelta);
                manipulationInProgress = false;
                return true;
            }
            return false;
        }

        private bool TryCancelManipulation()
        {
            if (manipulationInProgress)
            {
                InputSystem?.RaiseGestureCanceled(this, manipulationAction);
                manipulationInProgress = false;
                return true;
            }
            return false;
        }

        private bool TryStartNavigation()
        {
            if (!navigationInProgress)
            {
                InputSystem?.RaiseGestureStarted(this, navigationAction);
                navigationInProgress = true;

                currentRailsUsed = Vector3.one;
                UpdateNavigationRails();
                return true;
            }
            return false;
        }

        private void UpdateNavigation()
        {
            if (navigationInProgress)
            {
                UpdateNavigationRails();
                InputSystem?.RaiseGestureUpdated(this, navigationAction, navigationDelta);
            }
        }

        private bool TryCompleteNavigation()
        {
            if (navigationInProgress)
            {
                InputSystem?.RaiseGestureCompleted(this, navigationAction, navigationDelta);
                navigationInProgress = false;
                return true;
            }
            return false;
        }

        private bool TryCancelNavigation()
        {
            if (navigationInProgress)
            {
                InputSystem?.RaiseGestureCanceled(this, navigationAction);
                navigationInProgress = false;
                return true;
            }
            return false;
        }

        // If rails are used, test the delta for largest component and limit navigation to that axis
        private void UpdateNavigationRails()
        {
            if (useRailsNavigation && currentRailsUsed == Vector3.one)
            {
                if (Mathf.Abs(cumulativeDelta.x) >= navigationStartThreshold)
                {
                    currentRailsUsed = new Vector3(1, 0, 0);
                }
                else if (Mathf.Abs(cumulativeDelta.y) > navigationStartThreshold)
                {
                    currentRailsUsed = new Vector3(0, 1, 0);
                }
                else if (Mathf.Abs(cumulativeDelta.z) > navigationStartThreshold)
                {
                    currentRailsUsed = new Vector3(0, 0, 1);
                }
            }
        }
    }
}