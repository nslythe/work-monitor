using Newtonsoft.Json.Converters;

namespace work_monitor
{
    /// <summary>
    /// Writes a date as <c>yyyy-MM-dd</c>, keeping the history files compatible with the
    /// original date-only format.
    /// </summary>
    public sealed class IsoDateJsonConverter : IsoDateTimeConverter
    {
        public IsoDateJsonConverter()
        {
            DateTimeFormat = "yyyy-MM-dd";
        }
    }
}
