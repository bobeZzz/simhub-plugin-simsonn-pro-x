using System;
using System.Collections;
using System.Reflection;

namespace SimsonnPedals
{
    /// <summary>
    /// The player's iRacing car class from SimHub's raw iRacing data (iRacingSDK session info). SimHub's own CarClass
    /// comes from CarClassShortName, which iRacing leaves empty in single-class sessions such as test drives, but
    /// CarClassID is always there. Read by reflection, so the plugin needs no reference to SimHub's iRacing reader.
    /// </summary>
    public static class IRacingCarClass
    {
        /// <summary>Class id and short name (may be empty) of the player's car, or false if the raw data doesn't have them yet.</summary>
        public static bool TryRead(object raw, out long classId, out string shortName)
        {
            classId = 0;
            shortName = null;
            try
            {
                object driverInfo = Get(Get(raw, "SessionData"), "DriverInfo");
                if (driverInfo == null || !(Get(driverInfo, "Drivers") is IEnumerable drivers)) return false;
                long playerIdx = Convert.ToInt64(Get(driverInfo, "DriverCarIdx"));
                foreach (object driver in drivers)
                {
                    if (driver == null || Convert.ToInt64(Get(driver, "CarIdx")) != playerIdx) continue;
                    classId = Convert.ToInt64(Get(driver, "CarClassID"));
                    shortName = (Get(driver, "CarClassShortName") as string)?.Trim();
                    return classId != 0 || !string.IsNullOrEmpty(shortName);
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            {
            }
            return false;
        }

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(target);
            return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }
    }
}
