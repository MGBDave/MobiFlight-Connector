using System;
using System.Collections.Generic;

namespace MobiFlight.InputConfig
{
    public class RetriggerInputAction : InputAction
    {
        public new const String Label = "MobiFlight - Retrigger switches";
        public const String TYPE = "RetriggerInputAction";
        DateTime lastExecution = DateTime.MinValue;

        /// <summary>
        /// Raised right before the devices are asked to resend their button states,
        /// so the events that follow can be recognized as retriggered (see RetriggerEventPacer).
        /// </summary>
        public static event EventHandler RetriggerStarting;

        public override object Clone()
        {
            RetriggerInputAction clone = new RetriggerInputAction();
         
            return clone;
        }

        public override void ReadXml(System.Xml.XmlReader reader)
        {
            // Nothing to do
        }

        public override void WriteXml(System.Xml.XmlWriter writer)
        {
            writer.WriteAttributeString("type", TYPE);
        }

        public override void execute(
            CacheCollection cacheCollection,
            InputEventArgs args,
            List<ConfigRefValue> configRefs)
        {
            // only execute if not happened last 5 seconds
            if (DateTime.Now.Ticks  - lastExecution.Ticks < 50000000) return;

            RetriggerStarting?.Invoke(this, EventArgs.Empty);

            foreach (MobiFlightModule module in cacheCollection.moduleCache.GetModules()) 
            {
                module.Retrigger();
            }
            foreach (Joystick joystick in cacheCollection.joystickManager.GetJoysticks())
            {
                joystick.Retrigger();
            }

            lastExecution = DateTime.Now;
        }

        public override bool Equals(object obj)
        {
            return obj != null && obj is RetriggerInputAction;
        }
    }
}