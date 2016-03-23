﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Qoollo.MpegDash
{
    public class MediaPresentationDescription
    {
        private readonly Stream stream;

        private readonly Lazy<XElement> mpdTag;

        public MediaPresentationDescription(Stream mpdStream)
        {
            stream = mpdStream;

            mpdTag = new Lazy<XElement>(ReadMpdTag);
            periods = new Lazy<IEnumerable<MpdPeriod>>(ParsePeriods);
        }

        public string Type
        {
            get { return mpdTag.Value.Attribute("type").Value; }
        }

        public string Profiles
        {
            get { return mpdTag.Value.Attribute("profiles").Value; }
        }

        public TimeSpan MinBufferTime
        {
            get { return XmlConvert.ToTimeSpan(mpdTag.Value.Attribute("minBufferTime").Value); }
        }

        public TimeSpan MaxSegmentDuration
        {
            get { return XmlConvert.ToTimeSpan(mpdTag.Value.Attribute("maxSegmentDuration").Value); }
        }

        public DateTimeOffset AvailabilityStartTime
        {
            get { return DateTimeOffset.Parse(mpdTag.Value.Attribute("availabilityStartTime").Value); }
        }

        public TimeSpan MediaPresentationDuration
        {
            get { return XmlConvert.ToTimeSpan(mpdTag.Value.Attribute("mediaPresentationDuration").Value); }
        }

        public IEnumerable<MpdPeriod> Periods
        {
            get { return periods.Value; }
        }
        private readonly Lazy<IEnumerable<MpdPeriod>> periods;

        private XElement ReadMpdTag()
        {
            using (var reader = XmlReader.Create(stream))
            {
                reader.ReadToFollowing("MPD");
                return XNode.ReadFrom(reader) as XElement;
            }
        }

        private IEnumerable<MpdPeriod> ParsePeriods()
        {
            return mpdTag.Value.Elements()
                .Where(n => n.Name == "Period")
                .Select(n => new MpdPeriod(n));
        }
    }
}
