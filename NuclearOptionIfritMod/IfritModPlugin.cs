using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionIfritMod
{
    [BepInPlugin("com.custom.ifritmod", "Ifrit Override Mod", "1.0.0")]
    [BepInDependency("com.offiry.qol", BepInDependency.DependencyFlags.SoftDependency)]
    public class IfritModPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal const float TargetMaxSpeed = 1887f;
        internal const float DoubledMaxThrust = 200000f;
        internal const float DoubledAfterburnerThrust = 94000f;
        internal const float ScramjetMinMach = 4.5f;
        internal const float ScramjetMinAltM = 60000f * 0.3048f;
        internal const float ScramjetThrustPerEngine = 500000f;
        internal static bool scramjetActive = false;
        internal const string OriginalName = "Multirole1";
        internal const string CloneJsonKey = "Multirole1X";
        internal const string CloneUnitName = "KR-67X SuperIfrit";
        internal const string CloneAircraftName = "KR-67X";
        internal const float CloneCostMillions = 600f;
        internal static AircraftDefinition clonedDefinition = null;
        internal static AircraftDefinition originalDefinition = null;
        internal static bool nextSpawnIsClone = false;
        private void Awake()
        {
            Log = Logger;
            var harmony = new Harmony("com.custom.ifritmod");
            harmony.PatchAll();
            try
            {
                var method = typeof(Encyclopedia).GetMethod("AfterLoad",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (method == null)
                    method = typeof(Encyclopedia).GetMethod("AfterLoad",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new Type[] { typeof(Encyclopedia) }, null);
                if (method != null)
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(EncyclopediaClonePatch), "Postfix"));
                    Log.LogInfo("Patched Encyclopedia.AfterLoad for aircraft cloning.");
                }
                else Log.LogError("Could not find Encyclopedia.AfterLoad!");
            }
            catch (Exception e) { Log.LogError("Failed to patch Encyclopedia.AfterLoad: " + e); }
            Log.LogInfo("Ifrit Override Mod loaded.");
        }

        private static bool IsIfritX(Aircraft aircraft)
        {
            if (aircraft == null) return false;
            try
            {
                var def = aircraft.definition;
                if (def == null) return false;
                if (clonedDefinition != null && def == clonedDefinition) return true;
                if (def.jsonKey == CloneJsonKey) return true;
                var parms = def.aircraftParameters;
                return parms != null && parms.aircraftName != null &&
                       parms.aircraftName.IndexOf("KR-67X", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static readonly AnimationCurve flatAltitudeCurve;
        static IfritModPlugin()
        {
            flatAltitudeCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(50000f, 1f));
            flatAltitudeCurve.preWrapMode = WrapMode.ClampForever;
            flatAltitudeCurve.postWrapMode = WrapMode.ClampForever;
        }
        [HarmonyPatch(typeof(Turbojet), "FixedUpdate")]
        public static class TurbojetFixedUpdatePatch
        {
            private static readonly FieldInfo aircraftField = AccessTools.Field(typeof(Turbojet), "aircraft");
            private static readonly FieldInfo maxSpeedField = AccessTools.Field(typeof(Turbojet), "maxSpeed");
            private static readonly FieldInfo minDensityField = AccessTools.Field(typeof(Turbojet), "minDensity");
            private static readonly FieldInfo altThrustField = AccessTools.Field(typeof(Turbojet), "altitudeThrust");
            private static readonly HashSet<int> logged = new HashSet<int>();
            private static bool wasScramjet = false;

            public static void Prefix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                int id = __instance.GetInstanceID();
                bool first = !logged.Contains(id);
                float targetThrust = scramjetActive ? ScramjetThrustPerEngine : DoubledMaxThrust;
                if (Math.Abs(__instance.maxThrust - targetThrust) > 1f)
                {
                    if (first) Log.LogInfo(__instance.gameObject.name + " maxThrust -> " + targetThrust);
                    __instance.maxThrust = targetThrust;
                }
                if (maxSpeedField != null)
                {
                    float v = (float)maxSpeedField.GetValue(__instance);
                    if (Math.Abs(v - TargetMaxSpeed) > 1f)
                        maxSpeedField.SetValue(__instance, TargetMaxSpeed);
                }
                if (minDensityField != null)
                {
                    float v = (float)minDensityField.GetValue(__instance);
                    if (v > -0.5f) minDensityField.SetValue(__instance, -1f);
                }
                if (altThrustField != null)
                {
                    var curve = altThrustField.GetValue(__instance) as AnimationCurve;
                    if (curve != flatAltitudeCurve) altThrustField.SetValue(__instance, flatAltitudeCurve);
                }
                if (first) logged.Add(id);
            }

            public static void Postfix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                float altMeters = __instance.transform.position.y - Datum.originPosition.y;
                float speed = aircraft.speed;
                float sos = Mathf.Max(-0.005f * altMeters + 340f, 290f);
                float mach = speed / sos;
                bool shouldBeActive = mach >= ScramjetMinMach && altMeters >= ScramjetMinAltM;
                if (scramjetActive && !shouldBeActive)
                    scramjetActive = mach >= (ScramjetMinMach * 0.9f) && altMeters >= (ScramjetMinAltM * 0.9f);
                else
                    scramjetActive = shouldBeActive;
                if (scramjetActive != wasScramjet)
                {
                    Log.LogInfo("[Scramjet] " + (scramjetActive ? "ON" : "OFF") + " Mach " + mach.ToString("F2"));
                    wasScramjet = scramjetActive;
                }
            }
        }
        [HarmonyPatch(typeof(JetNozzle), "Thrust")]
        public static class JetNozzleThrustPatch
        {
            private static readonly FieldInfo abField = AccessTools.Field(typeof(JetNozzle), "afterburners");
            private static readonly FieldInfo abThrustField = AccessTools.Field(typeof(JetNozzle).GetNestedType("Afterburner"), "thrust");
            private static readonly FieldInfo totalThrustField = AccessTools.Field(typeof(JetNozzle), "totalThrust");
            private static readonly FieldInfo nozzleAircraftField = AccessTools.Field(typeof(JetNozzle), "aircraft");
            private static readonly HashSet<int> loggedPre = new HashSet<int>();

            public static void Prefix(JetNozzle __instance)
            {
                var aircraft = nozzleAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                int id = __instance.GetInstanceID();
                bool first = !loggedPre.Contains(id);
                if (abField == null || abThrustField == null) return;
                var afterburners = abField.GetValue(__instance) as Array;
                if (afterburners == null) return;
                foreach (var ab in afterburners)
                {
                    float old = (float)abThrustField.GetValue(ab);
                    if (Math.Abs(old - DoubledAfterburnerThrust) > 1f)
                        abThrustField.SetValue(ab, DoubledAfterburnerThrust);
                }
                if (first) loggedPre.Add(id);
            }

            public static void Postfix(JetNozzle __instance)
            {
                var aircraft = nozzleAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                float density = aircraft.airDensity;
                if (density >= 0.4f) return;
                if (abField == null || totalThrustField == null) return;
                var afterburners = abField.GetValue(__instance) as Array;
                if (afterburners == null) return;
                float abTotal = 0f;
                var getThrust = AccessTools.Method(typeof(JetNozzle).GetNestedType("Afterburner"), "GetThrust");
                if (getThrust == null) return;
                foreach (var ab in afterburners)
                    abTotal += (float)getThrust.Invoke(ab, null);
                float clamped = Mathf.Clamp(density, 0.4f, 1f);
                float correction = abTotal * (1f - clamped);
                if (correction > 0f)
                {
                    float current = (float)totalThrustField.GetValue(__instance);
                    totalThrustField.SetValue(__instance, current + correction);
                }
            }
        }
        [HarmonyPatch(typeof(SpeedGauge), "Refresh")]
        public static class SpeedGaugeOverspeedPatch
        {
            private static readonly FieldInfo thresholdField = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            private static readonly FieldInfo sgAircraftField = AccessTools.Field(typeof(SpeedGauge), "aircraft");
            private static readonly FieldInfo overspeedDisplayField = AccessTools.Field(typeof(SpeedGauge), "overspeedDisplay");
            private static readonly FieldInfo lastOverspeedField = AccessTools.Field(typeof(SpeedGauge), "lastOverspeed");
            private static readonly FieldInfo overspeedVoiceField = AccessTools.Field(typeof(SpeedGauge), "overspeedVoice");
            private static readonly FieldInfo airspeedDisplayField = AccessTools.Field(typeof(SpeedGauge), "airspeedDisplay");
            private static bool voiceNulled = false;

            public static void Prefix(SpeedGauge __instance)
            {
                if (thresholdField == null || sgAircraftField == null) return;
                var aircraft = sgAircraftField.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                thresholdField.SetValue(__instance, TargetMaxSpeed);
                if (!voiceNulled && overspeedVoiceField != null)
                {
                    overspeedVoiceField.SetValue(__instance, null);
                    voiceNulled = true;
                }
                if (lastOverspeedField != null)
                    lastOverspeedField.SetValue(__instance, Time.timeSinceLevelLoad);
            }

            public static void Postfix(SpeedGauge __instance)
            {
                if (sgAircraftField == null) return;
                var aircraft = sgAircraftField.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                if (overspeedDisplayField != null)
                {
                    var display = overspeedDisplayField.GetValue(__instance) as Text;
                    if (display != null && display.enabled) display.enabled = false;
                }
                if (airspeedDisplayField != null)
                {
                    var txt = airspeedDisplayField.GetValue(__instance) as Text;
                    if (txt != null && txt.color == Color.red) txt.color = Color.white;
                }
            }
        }

        [HarmonyPatch(typeof(SpeedGauge), "Initialize")]
        public static class SpeedGaugeInitPatch
        {
            private static readonly FieldInfo thresholdField = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            public static void Postfix(SpeedGauge __instance, Aircraft aircraft)
            {
                if (thresholdField == null || !IsIfritX(aircraft)) return;
                thresholdField.SetValue(__instance, TargetMaxSpeed);
            }
        }
        [HarmonyPatch(typeof(FlightHud), "Update")]
        public static class ScramjetHudPatch
        {
            private static GameObject hudObject;
            private static Text hudText;
            private static bool wasActive = false;
            private static float pulseTimer = 0f;

            public static void Postfix(FlightHud __instance)
            {
                if (hudObject == null)
                {
                    try
                    {
                        var canvasField = AccessTools.Field(typeof(FlightHud), "canvas");
                        if (canvasField == null) return;
                        var canvas = canvasField.GetValue(__instance) as Canvas;
                        if (canvas == null) return;
                        hudObject = new GameObject("ScramjetIndicator");
                        hudObject.transform.SetParent(canvas.transform, false);
                        hudText = hudObject.AddComponent<Text>();
                        hudText.text = "SCRAMJET ACTIVE";
                        hudText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        hudText.fontSize = 22;
                        hudText.fontStyle = FontStyle.Bold;
                        hudText.alignment = TextAnchor.MiddleCenter;
                        hudText.color = new Color(0f, 1f, 0.6f, 1f);
                        var outline = hudObject.AddComponent<Outline>();
                        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
                        outline.effectDistance = new Vector2(1.5f, -1.5f);
                        var rect = hudObject.GetComponent<RectTransform>();
                        rect.anchorMin = new Vector2(0.5f, 0.75f);
                        rect.anchorMax = new Vector2(0.5f, 0.75f);
                        rect.pivot = new Vector2(0.5f, 0.5f);
                        rect.anchoredPosition = Vector2.zero;
                        rect.sizeDelta = new Vector2(300f, 40f);
                        hudObject.SetActive(false);
                    }
                    catch { return; }
                }
                if (scramjetActive != wasActive)
                {
                    hudObject.SetActive(scramjetActive);
                    wasActive = scramjetActive;
                    if (scramjetActive) pulseTimer = 0f;
                }
                if (scramjetActive && hudText != null)
                {
                    pulseTimer += Time.deltaTime;
                    float alpha = 0.7f + 0.3f * Mathf.Sin(pulseTimer * 3f);
                    hudText.color = new Color(0f, 1f, 0.6f, alpha);
                }
            }
        }
        public static class EncyclopediaClonePatch
        {
            public static void Postfix(Encyclopedia __instance)
            {
                if (clonedDefinition != null) return;
                Log.LogInfo("[Clone] Encyclopedia.AfterLoad Postfix fired!");
                try
                {
                    AircraftDefinition original = null;
                    foreach (var def in __instance.aircraft)
                    {
                        if (def != null && def.name != null &&
                            def.name.IndexOf(OriginalName, StringComparison.OrdinalIgnoreCase) >= 0)
                        { original = def; break; }
                    }
                    if (original == null) { Log.LogError("[Clone] Could not find Multirole1!"); return; }
                    originalDefinition = original;

                    clonedDefinition = UnityEngine.Object.Instantiate(original);
                    clonedDefinition.name = CloneJsonKey;
                    clonedDefinition.jsonKey = CloneJsonKey;
                    clonedDefinition.unitName = CloneUnitName;
                    clonedDefinition.unitPrefab = original.unitPrefab;
                    clonedDefinition.value = CloneCostMillions;
                    clonedDefinition.description = "Scramjet-capable variant of the KR-67A Ifrit.";
                    clonedDefinition.code = CloneAircraftName;

                    clonedDefinition.aircraftParameters = UnityEngine.Object.Instantiate(original.aircraftParameters);
                    clonedDefinition.aircraftParameters.name = CloneJsonKey + "_parameters";
                    clonedDefinition.aircraftParameters.aircraftName = CloneAircraftName;
                    clonedDefinition.aircraftParameters.aircraftDescription =
                        "KR-67X SuperIfrit. Doubled thrust, Mach 5.5 capable, 85,000 ft ceiling with scramjet.";

                    clonedDefinition.aircraftParameters.rankRequired = 5;
                    if (original.aircraftInfo != null)
                    {
                        clonedDefinition.aircraftInfo = new AircraftInfo
                        {
                            emptyWeight = original.aircraftInfo.emptyWeight,
                            maxSpeed = TargetMaxSpeed,
                            stallSpeed = original.aircraftInfo.stallSpeed,
                            maneuverability = original.aircraftInfo.maneuverability,
                            maxWeight = original.aircraftInfo.maxWeight
                        };
                    }

                    __instance.aircraft.Add(clonedDefinition);
                    if (Encyclopedia.Lookup != null && !Encyclopedia.Lookup.ContainsKey(CloneJsonKey))
                        Encyclopedia.Lookup.Add(CloneJsonKey, clonedDefinition);
                    if (__instance.IndexLookup != null)
                    {
                        ((INetworkDefinition)clonedDefinition).LookupIndex = __instance.IndexLookup.Count;
                        __instance.IndexLookup.Add(clonedDefinition);
                    }
                    clonedDefinition.CacheMass();
                    Log.LogInfo("[Clone] Created " + CloneUnitName + " cost=" + CloneCostMillions + "m");
                }
                catch (Exception e) { Log.LogError("[Clone] Failed: " + e); }
            }
        }
        [HarmonyPatch(typeof(Hangar), "GetAvailableAircraft")]
        public static class HangarInjectClonePatch
        {
            private static readonly FieldInfo availField = AccessTools.Field(typeof(Hangar), "availableAircraft");

            public static void Postfix(Hangar __instance, ref AircraftDefinition[] __result)
            {
                if (clonedDefinition == null)
                {
                    try
                    {
                        var enc = Encyclopedia.i;
                        if (enc != null && enc.aircraft != null && enc.aircraft.Count > 0)
                            EncyclopediaClonePatch.Postfix(enc);
                    }
                    catch { }
                }
                if (clonedDefinition == null || __result == null || __result.Length == 0) return;
                bool hasIfrit = false;
                foreach (var def in __result)
                    if (def != null && def == originalDefinition) { hasIfrit = true; break; }
                if (!hasIfrit) return;
                foreach (var def in __result)
                    if (def == clonedDefinition) return;
                var expanded = new AircraftDefinition[__result.Length + 1];
                Array.Copy(__result, expanded, __result.Length);
                expanded[__result.Length] = clonedDefinition;
                __result = expanded;
                if (availField != null) availField.SetValue(__instance, expanded);
            }
        }

        [HarmonyPatch(typeof(Hangar), "CanSpawnAircraft")]
        public static class HangarCanSpawnClonePatch
        {
            public static void Postfix(Hangar __instance, AircraftDefinition definition, ref bool __result)
            {
                if (__result || clonedDefinition == null || definition != clonedDefinition) return;
                var field = AccessTools.Field(typeof(Hangar), "availableAircraft");
                if (field == null) return;
                var available = field.GetValue(__instance) as AircraftDefinition[];
                if (available == null) return;
                foreach (var def in available)
                {
                    if (def == originalDefinition || def == clonedDefinition)
                    {
                        var prop = AccessTools.Property(typeof(Hangar), "Available");
                        __result = prop != null ? (bool)prop.GetValue(__instance) : true;
                        return;
                    }
                }
            }
        }
        [HarmonyPatch(typeof(AircraftSelectionMenu), "SpawnPreview")]
        public static class SelectionMenuPreviewPatch
        {
            private static readonly FieldInfo selectedTypeField = AccessTools.Field(typeof(AircraftSelectionMenu), "selectedType");
            private static readonly FieldInfo selectionIndexField = AccessTools.Field(typeof(AircraftSelectionMenu), "selectionIndex");
            private static readonly FieldInfo aircraftSelectionField = AccessTools.Field(typeof(AircraftSelectionMenu), "aircraftSelection");
            private static readonly FieldInfo previewAircraftField = AccessTools.Field(typeof(AircraftSelectionMenu), "previewAircraft");
            private static readonly FieldInfo unitDefField = AccessTools.Field(typeof(Unit), "definition");

            public static void Postfix(AircraftSelectionMenu __instance)
            {
                if (clonedDefinition == null) return;
                var selection = aircraftSelectionField?.GetValue(__instance) as List<AircraftDefinition>;
                if (selection == null) return;
                int idx = (int)(selectionIndexField?.GetValue(__instance) ?? -1);
                if (idx < 0 || idx >= selection.Count) return;
                if (selection[idx] != clonedDefinition) return;
                var previewAircraft = previewAircraftField?.GetValue(__instance) as Aircraft;
                if (previewAircraft != null && unitDefField != null)
                    unitDefField.SetValue(previewAircraft, clonedDefinition);
                if (selectedTypeField != null)
                    selectedTypeField.SetValue(__instance, clonedDefinition);
                Log.LogInfo("[Preview] Fixed definition to clonedDefinition");
            }
        }

        [HarmonyPatch(typeof(Hangar), "TrySpawnAircraft")]
        public static class HangarTrySpawnFlagPatch
        {
            public static void Prefix(AircraftDefinition definition)
            {
                if (clonedDefinition != null && definition == clonedDefinition)
                {
                    nextSpawnIsClone = true;
                    Log.LogInfo("[Spawn] Flagged next spawn as KR-67X clone");
                }
            }
        }

        [HarmonyPatch(typeof(Spawner), "SpawnAircraft")]
        public static class SpawnerSpawnAircraftPatch
        {
            private static readonly FieldInfo unitDefField = AccessTools.Field(typeof(Unit), "definition");

            public static void Postfix(Aircraft __result)
            {
                if (__result == null || clonedDefinition == null || unitDefField == null) return;
                if (!nextSpawnIsClone) return;
                unitDefField.SetValue(__result, clonedDefinition);
                nextSpawnIsClone = false;
                Log.LogInfo("[Spawn] Reassigned definition to clonedDefinition");
            }
        }
    }
}
