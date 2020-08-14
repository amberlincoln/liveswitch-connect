﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FM.LiveSwitch.Connect
{
    class FFCapturer : Sender<FFCaptureOptions, AudioSource, VideoSource>
    {
        public AudioFormat RtpAudioFormat
        {
            get 
            {
                if (Options.AudioEncoding.HasValue)
                {
                    return Options.AudioEncoding.Value.CreateFormat(true);
                }
                return AudioFormat;
            }
        }

        public VideoFormat RtpVideoFormat
        {
            get
            {
                if (Options.VideoEncoding.HasValue)
                {
                    return Options.VideoEncoding.Value.CreateFormat(true);
                }
                return VideoFormat;
            }
        }

        public FFCapturer(FFCaptureOptions options)
            : base(options)
        { }

        private static string ShortId()
        {
            return Guid.NewGuid().ToString().Replace("-","").Substring(0, 8);
        }

        public Task<int> Capture()
        {
            if (!Options.NoAudio)
            {
                if (Options.AudioMode == FFCaptureMode.NoEncode && Options.AudioCodec == AudioCodec.Any)
                {
                    Console.Error.WriteLine("--audio-codec must be set (not 'any') if --audio-mode is 'noencode'.");
                    return Task.FromResult(1);
                }
                if (Options.AudioEncoding.HasValue)
                {
                    Options.AudioTranscode = true;
                }
            }
            if (!Options.NoVideo)
            {
                if (Options.VideoMode == FFCaptureMode.NoEncode && Options.VideoCodec == VideoCodec.Any)
                {
                    Console.Error.WriteLine("--video-codec must be set (not 'any') if --video-mode is 'noencode'.");
                    return Task.FromResult(1);
                }
                if (Options.VideoEncoding.HasValue)
                {
                    Options.VideoTranscode = true;
                }
            }
            return Send();
        }

        protected override AudioSource CreateAudioSource()
        {
            if (Options.AudioMode == FFCaptureMode.LSEncode)
            {
                var source = new PcmNamedPipeAudioSource($"ffcapture_pcm_{ShortId()}");
                source.OnPipeConnected += () =>
                {
                    Console.Error.WriteLine("Video pipe connected.");
                };
                return source;
            }
            else
            {
                return new RtpAudioSource(RtpAudioFormat);
            }
        }

        protected override VideoSource CreateVideoSource()
        {
            if (Options.VideoMode == FFCaptureMode.LSEncode)
            {
                var source = new Yuv4MpegNamedPipeVideoSource($"ffcapture_i420_{ShortId()}");
                source.OnPipeConnected += () =>
                {
                    Console.Error.WriteLine("Video pipe connected.");
                };
                return source;
            }
            else
            {
                return new RtpVideoSource(RtpVideoFormat);
            }
        }

        private Process FFmpeg;
        private const int G722PacketSize = 320 + 12;
        private const int PcmuPacketSize = 320 + 12;
        private const int PcmaPacketSize = 320 + 12;
        private const int Vp8PacketSize = 1000 + 12;
        private const int Vp9PacketSize = 1000 + 12;
        private const int H264PacketSize = 1000 + 12;

        protected override Task Ready()
        {
            var ready = base.Ready();

            var args = new List<string>
            {
                "-y",
                Options.InputArgs
            };

            if (AudioSource != null)
            {
                var config = AudioSource.Config;

                args.Add($"-map 0:a:0");

                if (Options.AudioMode == FFCaptureMode.LSEncode)
                {
                    var source = AudioSource as PcmNamedPipeAudioSource;
                    args.AddRange(new[]
                    {
                        $"-f s16le",
                        $"-ar {config.ClockRate}",
                        $"-ac {config.ChannelCount}",
                        NamedPipe.GetOSPipeName(source.PipeName)
                    });
                }
                else
                {
                    var source = AudioSource as RtpAudioSource;

                    args.Add($"-f rtp");

                    if (Options.AudioMode == FFCaptureMode.NoEncode)
                    {
                        args.Add($"-c copy");
                    }
                    else
                    {
                        if (RtpAudioFormat.IsOpus)
                        {
                            args.AddRange(new[]
                            {
                                $"-ar {config.ClockRate}",
                                $"-ac {config.ChannelCount}",
                                $"-c libopus",
                                $"-b:a {Options.AudioBitrate}k",
                            });
                        }
                        else if (RtpAudioFormat.IsG722)
                        {
                            args.AddRange(new[]
                            {
                                $"-ar 16000",
                                $"-ac 1",
                                $"-c g722",
                            });
                        }
                        else if (RtpAudioFormat.IsPcmu)
                        {
                            args.AddRange(new[]
                            {
                                $"-ar 8000",
                                $"-ac 1",
                                $"-c pcm_mulaw",
                            });
                        }
                        else if (RtpAudioFormat.IsPcma)
                        {
                            args.AddRange(new[]
                            {
                                $"-ar 8000",
                                $"-ac 1",
                                $"-c pcm_alaw",
                            });
                        }
                        else
                        {
                            throw new Exception("Unknown audio encoding.");
                        }
                    }

                    if (RtpAudioFormat.IsOpus)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}");
                    }
                    else if (RtpAudioFormat.IsG722)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={G722PacketSize}");
                    }
                    else if (RtpAudioFormat.IsPcmu)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={PcmuPacketSize}");
                    }
                    else if (RtpAudioFormat.IsPcma)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={PcmaPacketSize}");
                    }
                    else
                    {
                        throw new Exception("Unknown audio encoding.");
                    }
                }
            }

            var readH264ParameterSets = false;
            var sdpFileName = null as string;
            if (VideoSource != null)
            {
                args.Add($"-map 0:v:0");

                if (Options.VideoMode == FFCaptureMode.LSEncode)
                {
                    var source = VideoSource as Yuv4MpegNamedPipeVideoSource;
                    args.AddRange(new[]
                    {
                        $"-f yuv4mpegpipe",
                        $"-pix_fmt yuv420p",
                        NamedPipe.GetOSPipeName(source.PipeName)
                    });
                }
                else
                {
                    var source = VideoSource as RtpVideoSource;

                    args.Add($"-f rtp");

                    if (Options.VideoMode == FFCaptureMode.NoEncode)
                    {
                        args.Add($"-c copy");

                        if (RtpVideoFormat.IsH264)
                        {
                            readH264ParameterSets = true;
                            source.NeedsParameterSets = true;
                            sdpFileName = $"{VideoSource.Id}.sdp";
                            args.AddRange(new[]
                            {
                                $"-sdp_file {sdpFileName}",
                            });
                        }
                    }
                    else
                    {
                        if (RtpVideoFormat.IsVp8)
                        {
                            args.AddRange(new[]
                            {
                                $"-c libvpx -auto-alt-ref 0",
                                $"-pix_fmt yuv420p",
                                $"-quality realtime",
                                $"-speed 16",
                                $"-crf 10",
                                $"-b:v {Options.VideoBitrate}k",
                                $"-g {Options.FFEncodeKeyFrameInterval}",
                            });
                        }
                        else if (RtpVideoFormat.IsVp9)
                        {
                            args.AddRange(new[]
                            {
                                $"-c libvpx-vp9 -strict experimental",
                                $"-level 0",
                                $"-pix_fmt yuv420p",
                                $"-lag-in-frames 0",
                                $"-deadline realtime",
                                $"-quality realtime",
                                $"-speed 16",
                                $"-b:v {Options.VideoBitrate}k -maxrate {Options.VideoBitrate}k",
                                $"-g {Options.FFEncodeKeyFrameInterval}",
                            });
                        }
                        else if (RtpVideoFormat.IsH264)
                        {
                            args.AddRange(new[]
                            {
                                $"-c libx264",
                                $"-profile:v baseline",
                                $"-level:v 1.3",
                                $"-pix_fmt yuv420p",
                                $"-tune zerolatency",
                                $"-b:v {Options.VideoBitrate}k",
                                $"-g {Options.FFEncodeKeyFrameInterval} -keyint_min {Options.FFEncodeKeyFrameInterval}",
                            });
                        }
                        else
                        {
                            throw new Exception("Unknown video format.");
                        }
                    }

                    if (RtpVideoFormat.IsVp8)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={Vp8PacketSize}");
                    }
                    else if (RtpVideoFormat.IsVp9)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={Vp9PacketSize}");
                    }
                    else if (RtpVideoFormat.IsH264)
                    {
                        args.Add($"rtp://127.0.0.1:{source.Port}?pkt_size={H264PacketSize}");
                    }
                    else
                    {
                        throw new Exception("Unknown video format.");
                    }
                }
            }

            FFmpeg = FFUtility.FFmpeg(string.Join(" ", args));

            if (readH264ParameterSets)
            {
                ProcessParameterSets(sdpFileName);
            }

            return ready;
        }

        protected override Task Unready()
        {
            if (FFmpeg != null)
            {
                FFmpeg.StandardInput.Write('q');
                FFmpeg.WaitForExit();
            }

            return base.Unready();
        }

        private void ProcessParameterSets(string sdpFileName)
        {
            // wait for SDP file to exist
            var cancelTimeout = new CancellationTokenSource();
            var timeout = Task.Delay(5000, cancelTimeout.Token);
            while (!File.Exists(sdpFileName) && !timeout.IsCompletedSuccessfully)
            {
                Thread.Sleep(10);
            }
            if (timeout.IsCompletedSuccessfully)
            {
                throw new Exception("File containing H.264 parameter sets was not found.");
            }
            cancelTimeout.Cancel();

            // read SDP file
            cancelTimeout = new CancellationTokenSource();
            timeout = Task.Delay(5000, cancelTimeout.Token);
            string sdp = null;
            Exception readException = null;
            while (sdp == null && !timeout.IsCompletedSuccessfully)
            {
                try
                {
                    sdp = File.ReadAllText(sdpFileName);
                }
                catch (Exception ex)
                {
                    readException = ex;
                }
            }
            if (timeout.IsCompletedSuccessfully)
            {
                throw new Exception("File containing H.264 parameter sets could not be read.", readException);
            }
            cancelTimeout.Cancel();

            // remove header (removed in future release)
            // http://git.videolan.org/?p=ffmpeg.git;a=commitdiff;h=26c7f91e6624b1a46e39fb887b07b77db6cee328
            var header = "SDP:\n";
            if (sdp.StartsWith(header))
            {
                sdp = sdp.Substring(header.Length);
            }

            Console.Error.WriteLine($"H.264 SDP:{Environment.NewLine}{sdp}");

            // parse message
            var sdpMessage = Sdp.Message.Parse(sdp);
            if (sdpMessage == null)
            {
                throw new Exception("File containing H.264 parameter sets could not be parsed.");
            }

            var sdpMediaDescription = sdpMessage.MediaDescriptions.FirstOrDefault();
            if (sdpMediaDescription?.Media?.MediaType != Sdp.MediaType.Video)
            {
                throw new Exception("File containing H.264 parameter sets is missing video description.");
            }

            var rtpMapAttribute = sdpMediaDescription.GetRtpMapAttributes()?.FirstOrDefault();
            if (rtpMapAttribute?.FormatName != VideoFormat.H264Name)
            {
                throw new Exception("File containing H.264 parameter sets is missing H.264 RTP map attribute.");
            }

            var formatSpecificParametersString = rtpMapAttribute.RelatedFormatParametersAttribute?.FormatSpecificParameters;
            if (formatSpecificParametersString != null)
            {
                var formatSpecificParameters = DeserializeFormatSpecificParameters(formatSpecificParametersString);
                if (formatSpecificParameters.TryGetValue("sprop-parameter-sets", out var spropParameterSets))
                {
                    var encodedParameterSets = spropParameterSets.Split(',');
                    var decodedParameterSets = encodedParameterSets.Select(Base64.Decode);
                    var parameterSets = decodedParameterSets.Select(decodedParameterSet => DataBuffer.Wrap(decodedParameterSet)).ToArray();
                    var naluTypes = parameterSets.Select(parameterSet => parameterSet.Read8(0) & H264.Nalu.TypeMask);

                    Console.Error.WriteLine("H.264 parameter set types: " + string.Join(", ", naluTypes));

                    ((RtpVideoSource)VideoSource).ParameterSets = parameterSets;
                }
            }

            try
            {
                File.Delete(sdpFileName);
            }
            catch (Exception ex)
            {
                throw new Exception("File containing H.264 parameter sets could not be deleted.", ex);
            }
        }

        private Dictionary<string, string> DeserializeFormatSpecificParameters(string formatSpecificParametersString)
        {
            var formatSpecificParameters = new Dictionary<string, string>();

            if (formatSpecificParametersString != null)
            {
                var pairs = formatSpecificParametersString.Split(';');
                foreach (var pair in pairs)
                {
                    var trimmedPair = pair.Trim();
                    var equalsIndex = trimmedPair.IndexOf('=');
                    if (equalsIndex == -1)
                    {
                        formatSpecificParameters[trimmedPair] = null;
                    }
                    else
                    {
                        formatSpecificParameters[trimmedPair.Substring(0, equalsIndex)] = trimmedPair.Substring(equalsIndex + 1);
                    }
                }
            }

            return formatSpecificParameters;
        }
    }
}
