using System;
using Expanse.TrackingStation.Core;
using KSP.UI.Screens;
using UnityEngine;

namespace Expanse.TrackingStation.Adapter
{
    public sealed class TrackingActionResult
    {
        public bool Succeeded { get; private set; }
        public string Message { get; private set; }
        private TrackingActionResult(bool success, string message) { Succeeded = success; Message = message ?? string.Empty; }
        public static TrackingActionResult Success(string message) { return new TrackingActionResult(true, message); }
        public static TrackingActionResult Failure(string message) { return new TrackingActionResult(false, message); }
    }

    /// <summary>Small, explicit bridge to stock tracking actions. Selection is never an action here.</summary>
    public static class TrackingActions
    {
        public static TrackingActionResult FocusVessel(string vesselId)
        {
            if (!IsTrackingScene()) return TrackingActionResult.Failure("Focus is available in the Tracking Station scene only.");
            var vessel = KspSnapshotAdapter.ResolveVessel(vesselId);
            if (vessel == null) return TrackingActionResult.Failure("The selected vessel is no longer available.");
            try { if (vessel.mapObject == null) return TrackingActionResult.Failure("Stock has no map object for this vessel."); } catch { return TrackingActionResult.Failure("Stock map data is unavailable for this vessel."); }
            try
            {
                if (MapView.MapCamera == null || vessel.mapObject == null) return TrackingActionResult.Failure("No map object is available for this vessel.");
                MapView.MapCamera.SetTarget(vessel.mapObject);
                return TrackingActionResult.Success("Focused " + (vessel.vesselName ?? "vessel") + ".");
            }
            catch (Exception ex) { return TrackingActionResult.Failure("Focus failed: " + ex.Message); }
        }

        public static TrackingActionResult FocusBody(CelestialBody body)
        {
            if (!IsTrackingScene()) return TrackingActionResult.Failure("Focus is available in the Tracking Station scene only.");
            if (body == null) return TrackingActionResult.Failure("The selected body is unavailable.");
            try
            {
                if (MapView.MapCamera == null) return TrackingActionResult.Failure("The map camera is unavailable.");
                MapView.MapCamera.SetTarget(body);
                return TrackingActionResult.Success("Focused " + body.bodyName + ".");
            }
            catch (Exception ex) { return TrackingActionResult.Failure("Focus failed: " + ex.Message); }
        }

        /// <summary>
        /// Fly is intentionally the only operation that enters stock selection. The caller must
        /// invoke this only in response to a discrete Fly click, never from row selection or repaint.
        /// </summary>
        public static TrackingActionResult FlyVessel(string vesselId, string inputLockId)
        {
            if (!IsTrackingScene()) return TrackingActionResult.Failure("Fly is available in the Tracking Station scene only.");
            var vessel = KspSnapshotAdapter.ResolveVessel(vesselId);
            if (vessel == null) return TrackingActionResult.Failure("The selected vessel is no longer available.");
            try
            {
                var tracking = SpaceTracking.Instance;
                if (tracking == null) return TrackingActionResult.Failure("Stock Tracking Station is unavailable.");
                var button = tracking.FlyButton;
                if (button == null) return TrackingActionResult.Failure("Stock Fly is unavailable for this vessel.");
                if (inputLockId != null) InputLockManager.RemoveControlLock(inputLockId);
                if (InputLockManager.IsLocked(ControlTypes.TRACKINGSTATION_UI)) return TrackingActionResult.Failure("Stock Tracking Station controls remain locked.");
                if (vessel.mapObject == null || vessel.orbitRenderer == null)
                    return TrackingActionResult.Failure("Stock map data is not ready for this vessel.");
                tracking.SetVessel(vessel, true);
                if (InputLockManager.IsLocked(ControlTypes.TRACKINGSTATION_UI)) return TrackingActionResult.Failure("Stock controls became locked while selecting the vessel.");
                if (tracking.SelectedVessel == null || tracking.SelectedVessel.id.ToString() != vesselId)
                    return TrackingActionResult.Failure("Stock selection is still updating; try Fly again.");
                if (!button.IsInteractable() || !button.gameObject.activeInHierarchy || !button.isActiveAndEnabled)
                    return TrackingActionResult.Failure("Stock Fly is unavailable for this vessel.");
                button.onClick.Invoke();
                return TrackingActionResult.Success("Fly requested for " + (vessel.vesselName ?? "vessel") + ".");
            }
            catch (Exception ex) { return TrackingActionResult.Failure("Stock Fly failed: " + ex.Message); }
        }

        private static bool IsTrackingScene()
        {
            try { return HighLogic.LoadedScene == GameScenes.TRACKSTATION; }
            catch { return false; }
        }

        public static TrackingActionResult LeaveTrackingStation(string ownLockId)
        {
            if(!IsTrackingScene())return TrackingActionResult.Failure("Tracking Station is not active.");
            try
            {
                if(ownLockId!=null)InputLockManager.RemoveControlLock(ownLockId);
                if(InputLockManager.IsLocked(ControlTypes.TRACKINGSTATION_UI))return TrackingActionResult.Failure("Stock controls remain locked.");
                var tracking=SpaceTracking.Instance;var button=tracking==null?null:tracking.LeaveBtn;
                if(button==null||!button.isActiveAndEnabled||!button.IsInteractable())return TrackingActionResult.Failure("Stock Space Center action is unavailable.");
                button.onClick.Invoke();return TrackingActionResult.Success("Space Center requested.");
            }
            catch(Exception ex){return TrackingActionResult.Failure("Stock exit failed: "+ex.Message);}
        }

        public static TrackingActionResult InvokeNativeAction(string vesselId,string action,string ownLockId)
        {
            if(!IsTrackingScene())return TrackingActionResult.Failure("Tracking Station is not active.");
            var vessel=KspSnapshotAdapter.ResolveVessel(vesselId);
            if(vessel==null)return TrackingActionResult.Failure("The selected vessel is no longer available.");
            try
            {
                if(ownLockId!=null)InputLockManager.RemoveControlLock(ownLockId);
                if(InputLockManager.IsLocked(ControlTypes.TRACKINGSTATION_UI))return TrackingActionResult.Failure("Stock controls remain locked.");
                var tracking=SpaceTracking.Instance;
                if(tracking==null||vessel.mapObject==null||vessel.orbitRenderer==null)return TrackingActionResult.Failure("Stock tracking data is not ready.");
                tracking.SetVessel(vessel,true);
                if(InputLockManager.IsLocked(ControlTypes.TRACKINGSTATION_UI)||tracking.SelectedVessel==null||tracking.SelectedVessel.id!=vessel.id)return TrackingActionResult.Failure("Stock selection is still updating or locked.");
                var button=action=="Recover"?tracking.RecoverButton:action=="Track"?tracking.TrackButton:action=="Terminate"?tracking.DeleteButton:null;
                if(button==null||!button.isActiveAndEnabled||!button.gameObject.activeInHierarchy||!button.IsInteractable())return TrackingActionResult.Failure("Stock "+action+" is unavailable for this vessel.");
                button.onClick.Invoke();return TrackingActionResult.Success("Stock "+action+" requested; follow any stock confirmation.");
            }
            catch(Exception ex){return TrackingActionResult.Failure("Stock action failed: "+ex.Message);}
        }
    }
}
