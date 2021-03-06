﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Qoollo.MpegDash
{
    public class MpdDownloader
    {
        private readonly Uri mpdUrl;

        private readonly string destinationDir;

        private readonly Lazy<string> mpdFileName;

        private readonly Lazy<MediaPresentationDescription> mpd;

        private readonly Lazy<MpdWalker> walker;

        public MpdDownloader(Uri mpdUrl, string destinationDir)
        {
            this.mpdUrl = mpdUrl;
            this.destinationDir = destinationDir;

            mpdFileName = new Lazy<string>(GetMpdFileName);
            mpd = new Lazy<MediaPresentationDescription>(DownloadMpd);
            walker = new Lazy<MpdWalker>(CreateMpdWalker);
        }

        public IEnumerable<Track> GetTracksFor(TrackContentType type)
        {
            return walker.Value.GetTracksFor(type);
        }

        public Task<string> Download(TrackRepresentation trackRepresentation)
        {
            return Task.Factory.StartNew(() => DownloadTrackRepresentation(trackRepresentation, TimeSpan.Zero, TimeSpan.MaxValue));
        }

        public Task<string> Download(TrackRepresentation trackRepresentation, TimeSpan from, TimeSpan to)
        {
            return Task.Factory.StartNew(() => DownloadTrackRepresentation(trackRepresentation, from, to));
        }

        private string DownloadTrackRepresentation(TrackRepresentation trackRepresentation, TimeSpan from, TimeSpan to)
        {
            var files = new List<string>();

            var task = DownloadFragment(trackRepresentation.InitFragmentPath);
            task.Wait(TimeSpan.FromMinutes(5));
            if (task.Result)
            {
                files.Add(Path.Combine(destinationDir, GetLastPartOfPath(trackRepresentation.InitFragmentPath)));

                foreach (var fragmentPath in trackRepresentation.GetFragmentsPaths(from, to))
                {
                    task = DownloadFragment(fragmentPath);
                    task.Wait(TimeSpan.FromMinutes(5));
                    if (task.Result)
                        files.Add(Path.Combine(destinationDir, GetLastPartOfPath(fragmentPath)));
                    else
                        break;

                }
            }

            string outputFile = Path.Combine(destinationDir, DateTime.Now.ToString("yyyyMMddHHmmss") + "_video.mp4");
            CombineChunks(files, outputFile);

            DeleteAllFilesExcept(outputFile, destinationDir);

            return outputFile;
        }

        private void CombineChunks(IEnumerable<string> files, string outputFile)
        {
            using (var stream = File.OpenWrite(outputFile))
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var f in files)
                {
                    var bytes = File.ReadAllBytes(f);
                    var mdatBytes = Encoding.ASCII.GetBytes("mdat");
                    int offset = FindAtomOffset(bytes, mdatBytes);
                    //if (offset >= 0)
                    //    writer.Write(bytes, offset - mdatBytes.Length, bytes.Length - offset + mdatBytes.Length);
                    writer.Write(bytes);
                }
            }
        }

        private int FindAtomOffset(byte[] chunkBytes, byte[] atomBytes)
        {
            int mdatOffset = -1;
            for (int i = 0; i < chunkBytes.Length - atomBytes.Length && mdatOffset < 0; i++)
            {
                int matchCount = 0;
                for (int j = 0; j < atomBytes.Length; j++)
                {
                    if (chunkBytes[i + j] == atomBytes[j])
                        matchCount++;
                }
                if (matchCount == atomBytes.Length)
                    mdatOffset = i;
            }
            return mdatOffset;
        }

        private void DeleteAllFilesExcept(string outputFile, string destinationDir)
        {
            outputFile = Path.GetFullPath(outputFile);
            var files = Directory.GetFiles(destinationDir);
            mpd.Value.Dispose();
            foreach (var f in files)
            {
                string file = Path.GetFullPath(f);
                if (outputFile != file)
                    File.Delete(file);
            }
        }

        public Task<string> Download()
        {
            var tasks = new List<Task>();

            foreach (var period in mpd.Value.Periods)
            {
                foreach (var adaptationSet in period.AdaptationSets)
                {
                    foreach (var representation in adaptationSet.Representations)
                    {
                        tasks.Add(DownloadAllFragments(adaptationSet, representation));
                    }
                }
            }

            return Task.Factory.ContinueWhenAll(
                tasks.ToArray(),
                completed => CombineFragments(mpd.Value, mpdFileName.Value, Path.Combine(Path.GetDirectoryName(mpdFileName.Value), "video.mp4")));
        }

        private Task DownloadAllFragments(MpdAdaptationSet adaptationSet, MpdRepresentation representation)
        {
            return Task.Factory.StartNew(() => DownloadFragmentsUntilFirstFailure(adaptationSet, representation));
        }

        private string CombineFragments(MediaPresentationDescription mpd, string mpdFilePath, string outputFilePath)
        {
            var walker = new MpdWalker(mpd);
            var track = walker.GetTracksFor(TrackContentType.Video).First();
            var trackRepresentation = track.TrackRepresentations.OrderByDescending(r => r.Bandwidth).First();

            using (var stream = File.OpenWrite(outputFilePath))
            using (var writer = new BinaryWriter(stream))
            {
                string fragmentPath = Path.Combine(Path.GetDirectoryName(mpdFilePath), trackRepresentation.InitFragmentPath);
                writer.Write(File.ReadAllBytes(fragmentPath));

                foreach (var path in trackRepresentation.FragmentsPaths)
                {
                    fragmentPath = Path.Combine(Path.GetDirectoryName(mpdFilePath), path);
                    if (!File.Exists(fragmentPath))
                        break;
                    writer.Write(File.ReadAllBytes(fragmentPath));
                }
            }

            return outputFilePath;
        }

        private void DownloadFragmentsUntilFirstFailure(MpdAdaptationSet adaptationSet, MpdRepresentation representation)
        {
            var task = DownloadRepresentationInitFragment(adaptationSet, representation);

            if (TaskSucceded(task))
            {
                int i = 1;
                do
                {
                    task = DownloadRepresentationFragment(adaptationSet, representation, i);
                    i++;
                }
                while (TaskSucceded(task));
            }
        }

        private Task<bool> DownloadRepresentationInitFragment(MpdAdaptationSet adaptationSet, MpdRepresentation representation)
        {
            string initUrl = adaptationSet.SegmentTemplate.Initialization
                .Replace("$RepresentationID$", representation.Id);
            var task = DownloadFragment(initUrl);
            task.Wait(TimeSpan.FromMinutes(5));

            return task;
        }

        private Task<bool> DownloadRepresentationFragment(MpdAdaptationSet adaptationSet, MpdRepresentation representation, int index)
        {
            string fragmentUrl = adaptationSet.SegmentTemplate.Media
                        .Replace("$RepresentationID$", representation.Id)
                        .Replace("$Number$", index.ToString());

            var task = DownloadFragment(fragmentUrl);
            task.Wait(TimeSpan.FromMinutes(5));

            return task;
        }

        private Task<bool> DownloadFragment(string fragmentUrl)
        {
            using (var client = new WebClient())
            {
                var url = IsAbsoluteUrl(fragmentUrl)
                    ? new Uri(fragmentUrl)
                    : mpd.Value.BaseURL != null
                    ? new Uri(mpd.Value.BaseURL + fragmentUrl)
                    : new Uri(mpdUrl, fragmentUrl);

                string destPath = Path.Combine(destinationDir, GetLastPartOfPath(fragmentUrl));
                if (File.Exists(destPath))
                    destPath = Path.Combine(Path.GetDirectoryName(destPath), Path.ChangeExtension((Path.GetFileNameWithoutExtension(destPath) + "_1"), Path.GetExtension(destPath)));

                // create directory recursive
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));

                return Task.Factory.StartNew(() =>
                {
                    try
                    {
                        client.DownloadFile(url, destPath);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });
            }
        }

        private bool TaskSucceded(Task<bool> task)
        {
            return task.IsCompleted && !task.IsFaulted && task.Result;
        }

        private string GetMpdFileName()
        {
            string mpdFileName = mpdUrl.AbsolutePath;
            if (mpdFileName.Contains("/"))
                mpdFileName = mpdFileName.Substring(mpdFileName.LastIndexOf("/") + 1);
            string mpdPath = Path.Combine(destinationDir, mpdFileName);

            return mpdPath;
        }

        private MediaPresentationDescription DownloadMpd()
        {
            if (!Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);
            else
                Directory.GetFiles(destinationDir).ToList().ForEach(f => File.Delete(f));

            return MediaPresentationDescription.FromUrl(mpdUrl, mpdFileName.Value);
        }

        private MpdWalker CreateMpdWalker()
        {
            return new MpdWalker(mpd.Value);
        }

        private string GetLastPartOfPath(string url)
        {
            string fileName = url;
            if (IsAbsoluteUrl(url))
            {
                fileName = new Uri(url).AbsolutePath;
                if (fileName.Contains("/"))
                    fileName = fileName.Substring(fileName.LastIndexOf("/") + 1);
            }
            return fileName;
        }

        bool IsAbsoluteUrl(string url)
        {
            Uri result;
            return Uri.TryCreate(url, UriKind.Absolute, out result);
        }
    }
}
