using Newtonsoft.Json;
using NodaTime;
using NodaTime.Serialization.JsonNet;
using System;
using System.Collections.Generic;
using System.Text;

namespace FullCalendarIOtoiCalendarAF
{
    class SourceEvent
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("start_formatted")]
        public LocalDateTime Start { get; set; }

        [JsonProperty("end_formatted")]                
        public LocalDateTime? End { get; set; }

        [JsonProperty("allDay")]
        public bool IsAllDay { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

    }
}

