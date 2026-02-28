using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using NuclearOption.MissionEditorScripts.Buttons;

namespace NuclearOptionIfritMod
{
    [BepInPlugin("com.custom.ifritmod", "Ifrit Override Mod", "1.0.0")]
    [BepInDependency("com.offiry.qol", BepInDependency.DependencyFlags.SoftDependency)]
    public class IfritModPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal const float TargetMaxSpeed = 4500f;
        internal const float DoubledMaxThrust = 200000f;
        internal const float DoubledAfterburnerThrust = 94000f;
        internal const float ScramjetMinMach = 4.5f;
        internal const float ScramjetMinAltM = 60000f * 0.3048f;
        internal const float ScramjetThrustPerEngine = 500000f;
        internal const float FlameoutAltM = 164000f * 0.3048f;
        internal static bool scramjetActive = false;
        internal static bool flameout = false;
        internal const string OriginalName = "Multirole1";
        internal const string CloneJsonKey = "Multirole1X";
        internal const string CloneUnitName = "KR-67X SuperIfrit";
        internal const string CloneAircraftName = "KR-67X";
        internal const float CloneCostMillions = 1000f;
        internal static AircraftDefinition clonedDefinition = null;
        internal static AircraftDefinition originalDefinition = null;
        internal static bool nextSpawnIsClone = false;
        internal static BepInEx.Configuration.ConfigEntry<bool> DarkstarMode;
        internal static BepInEx.Configuration.ConfigEntry<float> ScimitarThrust;
        private void Awake()
        {
            Log = Logger;
            DarkstarMode = Config.Bind("General", "Darkstar Mode", false, "Double scramjet thrust when enabled.");
            ScimitarThrust = Config.Bind("Weapons", "Scimitar Thrust kN", 0f, "Override Scimitar motor thrust in kN. 0 = stock, 5000 = 5000kN.");
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

        // Identifies KR-67X aircraft by definition, jsonKey, aircraft name, or synced unitName.
        // The unitName fallback is critical for multiplayer: remote clients receive the
        // original Ifrit prefab (definition not reassigned), but the unitName SyncVar
        // contains "KR-67X" and is synced to all clients.
        private static bool IsIfritX(Aircraft aircraft)
        {
            if (aircraft == null) return false;
            try
            {
                var def = aircraft.definition;
                if (def != null)
                {
                    if (clonedDefinition != null && def == clonedDefinition) return true;
                    if (def.jsonKey == CloneJsonKey) return true;
                    var parms = def.aircraftParameters;
                    if (parms != null && parms.aircraftName != null &&
                        parms.aircraftName.IndexOf("KR-67X", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                // Fallback: check the network-synced unitName for multiplayer remote clients
                // where the definition field still points to the original Ifrit prefab
                var syncedName = aircraft.unitName;
                if (syncedName != null &&
                    syncedName.IndexOf("KR-67X", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                return false;
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
            private static readonly FieldInfo thrustField = AccessTools.Field(typeof(Turbojet), "thrust");
            private static readonly HashSet<int> logged = new HashSet<int>();
            private static bool wasScramjet = false;
            private static bool wasFlameout = false;
            private static float darkstarRampTimer = 0f;
            private const float DarkstarRampDuration = 10f;

            public static void Prefix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsIfritX(aircraft)) return;
                int id = __instance.GetInstanceID();
                bool first = !logged.Contains(id);

                float altMeters = __instance.transform.position.y - Datum.originPosition.y;
                flameout = altMeters >= FlameoutAltM;

                if (flameout)
                {
                    __instance.maxThrust = 0f;
                    if (minDensityField != null) minDensityField.SetValue(__instance, 999f);
                    if (flameout != wasFlameout)
                    {
                        Log.LogInfo("[Flameout] Engine flameout at " + altMeters.ToString("F0") + "m (" + (altMeters / 0.3048f).ToString("F0") + "ft)");
                        wasFlameout = flameout;
                    }
                    if (first) logged.Add(id);
                    return;
                }
                if (flameout != wasFlameout)
                {
                    Log.LogInfo("[Flameout] Engine relight at " + altMeters.ToString("F0") + "m (" + (altMeters / 0.3048f).ToString("F0") + "ft)");
                    wasFlameout = flameout;
                }

                // Darkstar mode: ramp the bonus thrust over 10 seconds
                float scramThrust = ScramjetThrustPerEngine;
                if (DarkstarMode.Value && scramjetActive)
                {
                    darkstarRampTimer = Mathf.Min(darkstarRampTimer + Time.fixedDeltaTime, DarkstarRampDuration);
                    float ramp = darkstarRampTimer / DarkstarRampDuration;
                    scramThrust = ScramjetThrustPerEngine + ScramjetThrustPerEngine * ramp;
                }
                else if (!scramjetActive)
                {
                    darkstarRampTimer = 0f;
                }
                float targetThrust = scramjetActive ? scramThrust : DoubledMaxThrust;
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

                if (flameout)
                {
                    scramjetActive = false;
                    if (thrustField != null) thrustField.SetValue(__instance, 0f);
                    return;
                }

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
                if (flameout) return;
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
                if (flameout) return;
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
                        "KR-67X SuperIfrit. Doubled thrust, Mach 10+ capable, 164,000 ft ceiling with scramjet.";

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

        // Multiplayer fix: on remote clients, the spawned prefab has the original Ifrit
        // definition baked in (definition is not a SyncVar). This patch runs on all clients
        // after SyncVars are applied and reassigns the cloned definition if the synced
        // unitName contains "KR-67X". This ensures IsIfritX() and all mod patches work
        // correctly on remote clients in multiplayer.
        [HarmonyPatch(typeof(Aircraft), "OnStartClient")]
        public static class AircraftClientFixupPatch
        {
            private static readonly FieldInfo unitDefField = AccessTools.Field(typeof(Unit), "definition");

            public static void Postfix(Aircraft __instance)
            {
                if (clonedDefinition == null || unitDefField == null) return;
                // Skip if definition is already correct (server/owning client)
                var currentDef = __instance.definition;
                if (currentDef == clonedDefinition) return;
                // Check the network-synced unitName to identify KR-67X on remote clients
                var syncedName = __instance.unitName;
                if (syncedName != null &&
                    syncedName.IndexOf("KR-67X", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    unitDefField.SetValue(__instance, clonedDefinition);
                    Log.LogInfo("[MP] Fixed KR-67X definition on client for: " + syncedName);
                }
            }
        }

        // Prevent flap deployment above Mach 1 on KR-67X
        [HarmonyPatch(typeof(ControlSurface), "UpdateJobFields")]
        public static class FlapSpeedLimitPatch
        {
            private static readonly FieldInfo csAircraftField = AccessTools.Field(typeof(ControlSurface), "aircraft");
            private static readonly FieldInfo csFlapField = AccessTools.Field(typeof(ControlSurface), "flap");
            private static LandingGear.GearState savedGearState;

            public static void Prefix(ControlSurface __instance)
            {
                if (csFlapField == null || csAircraftField == null) return;
                bool isFlap = (bool)csFlapField.GetValue(__instance);
                if (!isFlap) return;
                var aircraft = csAircraftField.GetValue(__instance) as Aircraft;
                if (aircraft == null || !IsIfritX(aircraft)) return;
                savedGearState = aircraft.gearState;
                float altMeters = aircraft.transform.position.y - Datum.originPosition.y;
                float sos = Mathf.Max(-0.005f * altMeters + 340f, 290f);
                if (aircraft.speed > sos)
                    aircraft.gearState = LandingGear.GearState.LockedRetracted;
            }

            public static void Postfix(ControlSurface __instance)
            {
                if (csFlapField == null || csAircraftField == null) return;
                bool isFlap = (bool)csFlapField.GetValue(__instance);
                if (!isFlap) return;
                var aircraft = csAircraftField.GetValue(__instance) as Aircraft;
                if (aircraft == null || !IsIfritX(aircraft)) return;
                aircraft.gearState = savedGearState;
            }
        }

        // Dampen RelaxedStabilityController at hypersonic speeds to prevent oscillations
        [HarmonyPatch(typeof(RelaxedStabilityController), "FilterInput")]
        public static class RelaxedStabilityHypersonicPatch
        {
            private static readonly FieldInfo rscCanardField = AccessTools.Field(typeof(RelaxedStabilityController), "canardRange");

            public static void Prefix(RelaxedStabilityController __instance, ControlInputs inputs, Rigidbody rb, ref float rawPitch)
            {
                // Get the aircraft from the rigidbody's parent
                var aircraft = rb.GetComponent<Aircraft>();
                if (aircraft == null || !IsIfritX(aircraft)) return;

                float altMeters = rb.transform.position.y - Datum.originPosition.y;
                float sos = Mathf.Max(-0.005f * altMeters + 340f, 290f);
                float mach = aircraft.speed / sos;

                // Above Mach 2, progressively reduce the RSC effect by widening canardRange
                // This makes the AoA-to-canardRange ratio smaller, reducing correction magnitude
                if (mach > 2f && rscCanardField != null)
                {
                    float origRange = (float)rscCanardField.GetValue(__instance);
                    // Scale canardRange up by mach factor: at Mach 5 it's 2.5x wider, at Mach 10 it's 5x
                    float scale = 1f + (mach - 2f) * 0.5f;
                    rscCanardField.SetValue(__instance, origRange * scale);
                }
            }

            public static void Postfix(RelaxedStabilityController __instance, Rigidbody rb)
            {
                var aircraft = rb.GetComponent<Aircraft>();
                if (aircraft == null || !IsIfritX(aircraft)) return;

                // Restore original canardRange (we need to undo the scaling)
                // Since we don't store it, recalculate: divide by the same scale
                if (rscCanardField != null)
                {
                    float altMeters = rb.transform.position.y - Datum.originPosition.y;
                    float sos = Mathf.Max(-0.005f * altMeters + 340f, 290f);
                    float mach = aircraft.speed / sos;
                    if (mach > 2f)
                    {
                        float scale = 1f + (mach - 2f) * 0.5f;
                        float current = (float)rscCanardField.GetValue(__instance);
                        rscCanardField.SetValue(__instance, current / scale);
                    }
                }
            }
        }
        // Dampen FlyByWire pitch response at hypersonic speeds to prevent oscillations
        // The FBW uses speed * airDensity / 1.225 as effective speed, which is low at altitude
        // but actual aero forces (density * speed^2) are still huge at hypersonic speeds
        [HarmonyPatch(typeof(ControlsFilter), "Filter")]
        public static class FBWHypersonicDamperPatch
        {
            public static void Postfix(ControlInputs inputs, Vector3 rawInputs, Rigidbody rb)
            {
                var aircraft = rb.GetComponent<Aircraft>();
                if (aircraft == null || !IsIfritX(aircraft)) return;

                float altMeters = rb.transform.position.y - Datum.originPosition.y;
                float sos = Mathf.Max(-0.005f * altMeters + 340f, 290f);
                float mach = aircraft.speed / sos;

                // Above Mach 3, progressively dampen pitch and roll inputs
                // At Mach 3: factor = 1.0 (no change)
                // At Mach 6: factor = 0.33
                // At Mach 10: factor = 0.2
                if (mach > 3f)
                {
                    float damper = 3f / mach;
                    inputs.pitch *= damper;
                    inputs.roll *= damper;
                }
            }
        }
        // Strengthen airbrakes on KR-67X to survive Mach 5 deployment at sea level
        // Reinforces joints and caps drag force to prevent structural failure
        [HarmonyPatch(typeof(Airbrake), "FixedUpdate")]
        public static class AirbrakeStrengthPatch
        {
            private static readonly FieldInfo abAircraftField = AccessTools.Field(typeof(Airbrake), "aircraft");
            private static readonly FieldInfo abPartField = AccessTools.Field(typeof(Airbrake), "part");
            private static readonly FieldInfo abDragField = AccessTools.Field(typeof(Airbrake), "dragAmount");
            private static readonly FieldInfo abOpenField = AccessTools.Field(typeof(Airbrake), "openAmount");
            private static readonly FieldInfo abAttachedField = AccessTools.Field(typeof(Airbrake), "attachedAircraft");
            private static readonly HashSet<int> reinforced = new HashSet<int>();
            // Max drag force in Newtons — tuned so airbrake is effective but won't rip off
            // At Mach 5 sea level: density=1.225, v=1700m/s, v²=2890000
            // Stock dragAmount might be ~0.1-0.5, giving 354k-1.77M N raw force
            // We cap total force to prevent joint failure while still providing strong braking
            private const float MaxBrakeForceN = 500000f;

            public static void Prefix(Airbrake __instance)
            {
                var aircraft = abAttachedField?.GetValue(__instance) as Aircraft;
                if (aircraft == null) aircraft = abAircraftField?.GetValue(__instance) as Aircraft;
                if (aircraft == null || !IsIfritX(aircraft)) return;

                // Reinforce joints once
                int id = __instance.GetInstanceID();
                if (!reinforced.Contains(id))
                {
                    var part = abPartField?.GetValue(__instance) as UnitPart;
                    if (part != null)
                    {
                        var joints = part.GetComponents<FixedJoint>();
                        foreach (var j in joints)
                        {
                            j.breakForce = float.PositiveInfinity;
                            j.breakTorque = float.PositiveInfinity;
                        }
                        Log.LogInfo("[Airbrake] Reinforced joints on " + __instance.gameObject.name);
                    }
                    reinforced.Add(id);
                }
            }

            public static void Postfix(Airbrake __instance)
            {
                // The base FixedUpdate already applied the force. We can't undo it,
                // but we reinforced the joints so it won't break.
                // For additional safety, we could also reduce drag at extreme speeds
                // but with infinite joint strength, the airbrake will hold.
            }
        }
        // Override Scimitar missile motor thrust if configured
        [HarmonyPatch(typeof(Missile), "MotorThrust")]
        public static class ScimitarThrustPatch
        {
            private static readonly FieldInfo missileInfoField = AccessTools.Field(typeof(Missile), "info");
            private static readonly FieldInfo missileMotorsField = AccessTools.Field(typeof(Missile), "motors");
            private static readonly Type motorType = typeof(Missile).GetNestedType("Motor", BindingFlags.NonPublic);
            private static readonly FieldInfo motorThrustField = motorType != null ? AccessTools.Field(motorType, "thrust") : null;
            private static readonly HashSet<int> patched = new HashSet<int>();

            public static void Prefix(Missile __instance)
            {
                if (ScimitarThrust == null || ScimitarThrust.Value <= 0f) return;
                int id = __instance.GetInstanceID();
                if (patched.Contains(id)) return;

                var info = missileInfoField?.GetValue(__instance) as WeaponInfo;
                if (info == null || info.weaponName == null) return;
                if (info.weaponName.IndexOf("Scimitar", StringComparison.OrdinalIgnoreCase) < 0 && info.weaponName.IndexOf("AAM-36", StringComparison.OrdinalIgnoreCase) < 0) return;

                if (missileMotorsField == null || motorThrustField == null) return;
                var motors = missileMotorsField.GetValue(__instance) as Array;
                if (motors == null) return;

                float thrustN = ScimitarThrust.Value * 1000f; // config is in kN
                foreach (var motor in motors)
                {
                    float old = (float)motorThrustField.GetValue(motor);
                    motorThrustField.SetValue(motor, thrustN);
                    Log.LogInfo("[Scimitar] Motor thrust: " + old + " -> " + thrustN + "N");
                }
                patched.Add(id);
            }
        }
        // Inject KR-67X into mission editor by clearing the static unit provider cache
        // so NewUnitPanel rebuilds it with our clone included from Encyclopedia.aircraft
        [HarmonyPatch(typeof(NewUnitPanel), "Awake")]
        public static class MissionEditorInjectPatch
        {
            private static readonly FieldInfo unitProvidersField =
                AccessTools.Field(typeof(NewUnitPanel), "unitProviders");

            public static void Prefix()
            {
                // Clear the static cache so it rebuilds with our clone in Encyclopedia.aircraft
                if (unitProvidersField != null)
                {
                    var dict = unitProvidersField.GetValue(null) as System.Collections.IDictionary;
                    if (dict != null && dict.Count > 0)
                    {
                        dict.Clear();
                        Log.LogInfo("[Editor] Cleared unitProviders cache for KR-67X injection");
                    }
                }

                // Ensure clone exists in Encyclopedia before the panel rebuilds
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
            }
        }

        // Scale Scimitar missile torque and maxTurnRate when thrust is overridden
        // so the missile can still track targets at much higher speeds
        [HarmonyPatch(typeof(Missile), "StartMissile")]
        public static class ScimitarGuidancePatch
        {
            private static readonly FieldInfo missileInfoField = AccessTools.Field(typeof(Missile), "info");
            private static readonly FieldInfo missileTorqueField = AccessTools.Field(typeof(Missile), "torque");
            private static readonly FieldInfo missileMaxTurnField = AccessTools.Field(typeof(Missile), "maxTurnRate");
            private static readonly FieldInfo missileMotorsField = AccessTools.Field(typeof(Missile), "motors");
            private static readonly Type motorType = typeof(Missile).GetNestedType("Motor", BindingFlags.NonPublic);
            private static readonly FieldInfo motorThrustField = motorType != null ? AccessTools.Field(motorType, "thrust") : null;

            public static void Postfix(Missile __instance)
            {
                if (ScimitarThrust == null || ScimitarThrust.Value <= 0f) return;

                var info = missileInfoField?.GetValue(__instance) as WeaponInfo;
                if (info == null || info.weaponName == null) return;
                if (info.weaponName.IndexOf("Scimitar", StringComparison.OrdinalIgnoreCase) < 0
                    && info.weaponName.IndexOf("AAM-36", StringComparison.OrdinalIgnoreCase) < 0) return;

                // Calculate thrust ratio to scale guidance proportionally
                float targetThrustN = ScimitarThrust.Value * 1000f;
                if (missileMotorsField == null || motorThrustField == null) return;
                var motors = missileMotorsField.GetValue(__instance) as Array;
                if (motors == null || motors.Length == 0) return;
                float stockThrust = (float)motorThrustField.GetValue(motors.GetValue(0));
                if (stockThrust <= 0f) stockThrust = 1f;
                float thrustRatio = targetThrustN / stockThrust;

                // Scale torque by sqrt of thrust ratio — more torque for faster turning
                // but not linearly, since aero forces also help at higher speed
                if (missileTorqueField != null)
                {
                    float oldTorque = (float)missileTorqueField.GetValue(__instance);
                    float newTorque = oldTorque * Mathf.Sqrt(thrustRatio);
                    missileTorqueField.SetValue(__instance, newTorque);
                    Log.LogInfo("[Scimitar] Torque: " + oldTorque + " -> " + newTorque);
                }

                // Scale maxTurnRate — allow wider PID steering commands
                if (missileMaxTurnField != null)
                {
                    float oldRate = (float)missileMaxTurnField.GetValue(__instance);
                    float newRate = oldRate * Mathf.Sqrt(thrustRatio);
                    missileMaxTurnField.SetValue(__instance, newRate);
                    // Also update the PID pLimit which was set from maxTurnRate in StartMissile
                    __instance.SetTorque(
                        (float)missileTorqueField.GetValue(__instance),
                        newRate);
                    Log.LogInfo("[Scimitar] MaxTurnRate: " + oldRate + " -> " + newRate);
                }
            }
        }
        // Double radar range on KR-67X
        [HarmonyPatch(typeof(Radar), "Awake")]
        public static class RadarRangePatch
        {
            private static readonly FieldInfo radarUnitField = AccessTools.Field(typeof(TargetDetector), "attachedUnit");

            public static void Postfix(Radar __instance)
            {
                var unit = radarUnitField?.GetValue(__instance) as Unit;
                if (unit == null) return;
                var aircraft = unit as Aircraft;
                if (aircraft == null || !IsIfritX(aircraft)) return;

                // Double the radar max range
                float oldRange = __instance.RadarParameters.maxRange;
                __instance.RadarParameters.maxRange = oldRange * 2f;
                // Double radar signal strength for better detection through clutter/jamming
                float oldSignal = __instance.RadarParameters.maxSignal;
                __instance.RadarParameters.maxSignal = oldSignal * 2f;
                Log.LogInfo("[Radar] KR-67X radar range: " + oldRange + " -> " + __instance.RadarParameters.maxRange + ", signal: " + oldSignal + " -> " + __instance.RadarParameters.maxSignal);
            }
        }
    }
}
