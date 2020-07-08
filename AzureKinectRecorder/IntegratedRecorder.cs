﻿using K4AdotNet.Record;
using K4AdotNet.Sensor;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AzureKinectRecorder
{
    class IntegratedRecorder:IDisposable
    {
        Recorder cameraRecorder;
        public Device camera;
        DeviceConfiguration cameraConfig;
        MMDevice microphone;
        String recordDirectory;
        /// <summary>
        /// This will be used to format part of the file name
        /// </summary>
        private Field field;

        String imageFullFileName;
        //String audioFullFillName;

        // For audio recording
        public IWaveIn audioCaptureDevice;
        private List<WaveFileWriter> audioFileWriters;
        String siteID;
        private int indOfNextChannelToWrite = 0;
        private int bytesPerSample;
        private Mutex mutAudioFileProcess;
        private List<WaveInEventArgs> audioBufferToRecord;
        private Queue<WaveInEventArgs> qAudioBufferToRecord;
        private Thread threadAudioRecording;
        private Queue<WaveInEventArgs> qAudioBufferToDisplay;
        private Thread threadAudioDisplay;
        public event EventHandler<WaveInEventArgs> AudioDataAvailable;
        public event EventHandler<System.Drawing.Image> VideoDataAvailable;
        private Thread threadVideoRecording;
        private Thread threadVideoDisplay;
        private Thread threadVideoFrameExtract;
        private Mutex mutVideoRecord;
        private Mutex mutVideoDisplay;
        private Queue<Capture> qVideoBufferToDisplay;
        private Queue<Capture> qVideoBufferToRecord;
        public double fpsProduced = 0;
        private bool isDisposing = false;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="camera"></param>
        /// <param name="cameraConfig"></param>
        /// <param name="deviceLabel">This will be incoporated into the file name recorded</param>
        /// <param name="mic"></param>
        public IntegratedRecorder(Device camera, Field field, MMDevice mic) {
            this.camera = camera;
            this.microphone = mic;
            this.field = field;
            mutAudioFileProcess = new Mutex();
            if (audioCaptureDevice == null)
            {
                audioCaptureDevice = CreateWaveInDevice();
            }

            DepthMode depthMode = DepthMode.Off;
            ColorResolution ColorResolution = ColorResolution.R720p;
            FrameRate frameRate = FrameRate.Thirty;
            cameraConfig = new DeviceConfiguration
            {
                CameraFps = frameRate,
                ColorFormat = ImageFormat.ColorMjpg,
                ColorResolution = ColorResolution,
                DepthMode = depthMode,
                WiredSyncMode = WiredSyncMode.Standalone,
            };
            this.camera.StartCameras(cameraConfig);

            // Forcibly turn on the microphone (some programs (Skype) turn it off).
            microphone.AudioEndpointVolume.Mute = false;
            // Not really start to record, while just for enabling calculating the volume peak value
            // refer to: https://github.com/naudio/NAudio/blob/master/Docs/RecordingLevelMeter.md
            audioCaptureDevice.StartRecording();

            qVideoBufferToDisplay = new Queue<Capture>();
            mutVideoRecord = new Mutex();
            mutVideoDisplay = new Mutex();
            threadVideoFrameExtract = new Thread(() => ImageExtractLoop());
            threadVideoFrameExtract.Start();
            threadVideoDisplay = new Thread(() => VideoDisplayLoop());
            threadVideoDisplay.Start();

            qAudioBufferToDisplay = new Queue<WaveInEventArgs>();
            threadAudioDisplay = new Thread(() => AudioDisplay());
            threadAudioDisplay.Start();
        }

        public void InitializeRecorder(String recordDirectory, String siteID) {
            this.recordDirectory = recordDirectory;
            this.siteID = siteID;
            var fileName = siteID + "_" + DateTime.Now.ToString("yyyyMMddHHmm") + "_" + field.ToString();
            indOfNextChannelToWrite = 0;
            bytesPerSample = audioCaptureDevice.WaveFormat.BitsPerSample / 8;
            imageFullFileName = Path.Combine(recordDirectory, $"{fileName}.mkv");
            audioFileWriters = new List<WaveFileWriter>();
            audioBufferToRecord = new List<WaveInEventArgs>();
            qAudioBufferToRecord = new Queue<WaveInEventArgs>();
            qVideoBufferToRecord = new Queue<Capture>();
            var wf_original = audioCaptureDevice.WaveFormat;
            var wf = WaveFormat.CreateCustomFormat(
                wf_original.Encoding, 
                wf_original.SampleRate, 
                1,
                wf_original.AverageBytesPerSecond / wf_original.Channels, 
                wf_original.BlockAlign / wf_original.Channels, 
                wf_original.BitsPerSample);
            for (int i = 0; i < wf_original.Channels; i++) {
                String audioFullFillName = Path.Combine(recordDirectory, $"{fileName}_channel_{i}.wav");
                WaveFileWriter writer = new WaveFileWriter(audioFullFillName, wf);
                audioFileWriters.Add(writer);
            }
        }

        //private void ReInitializeAudioWriter() {
        //    var fileName = siteID + "_" + DateTime.Now.ToString("yyyyMMddHHmm") + "_" + field.ToString();
        //    audioFullFillName = Path.Combine(recordDirectory, $"{fileName}.wav");
        //    audioFileWriter = new WaveFileWriter(audioFullFillName, audioCaptureDevice.WaveFormat);
        //}

        public void StartRecord() {
            // get the file name
            cameraRecorder = new Recorder(imageFullFileName, camera, cameraConfig);
            cameraRecorder.WriteHeader();
        }

        private IWaveIn CreateWaveInDevice()
        {
            IWaveIn newWaveIn;
            // can't set WaveFormat as WASAPI doesn't support SRC
            newWaveIn = new WasapiCapture(microphone);
            newWaveIn.DataAvailable += OnAudioDataAvailable;
            //newWaveIn.RecordingStopped += OnRecordingStopped;
            return newWaveIn;
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void VideoRecordingLoop()
        {
            while (true)
            {
                if (qVideoBufferToRecord.Count > 0)
                {
                    mutVideoRecord.WaitOne();
                    var capture = qVideoBufferToRecord.Dequeue();
                    mutVideoRecord.ReleaseMutex();

                    cameraRecorder.WriteCapture(capture);
                    cameraRecorder.Flush();

                    mutVideoDisplay.WaitOne();
                    qVideoBufferToDisplay.Enqueue(capture);
                    mutVideoDisplay.ReleaseMutex();
                }
                else
                {
                    if (Globals.getInstance().isRecording)
                    {
                        Thread.Sleep(100);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            while (qVideoBufferToRecord.Count > 0)
            {
                var capture = qVideoBufferToRecord.Dequeue();
                cameraRecorder.WriteCapture(capture);
                capture.Dispose();
            }
        }

        private void AudioRecordingProcess() {
            while(true) {
                if (qAudioBufferToRecord.Count > 0)
                {
                    mutAudioFileProcess.WaitOne();
                    var curBuffer = qAudioBufferToRecord.Dequeue();
                    mutAudioFileProcess.ReleaseMutex();
                    for (int i = 0; i < curBuffer.BytesRecorded / bytesPerSample; i++)
                    {
                        audioFileWriters[indOfNextChannelToWrite].Write(curBuffer.Buffer, i * bytesPerSample, bytesPerSample);
                        indOfNextChannelToWrite++;
                        indOfNextChannelToWrite %= audioCaptureDevice.WaveFormat.Channels;
                    }
                }
                else
                {
                    if (Globals.getInstance().isRecording)
                    {
                        Thread.Sleep(100);
                    }
                    else {
                        break;
                    }
                }
            }
            foreach (var writer in audioFileWriters)
            {
                writer.Flush();
                writer.Dispose();
            }
            audioFileWriters = null;
            qAudioBufferToRecord.Clear();
            qAudioBufferToRecord = null;
        }

        private void ImageExtractLoop()
        {
            Stopwatch sw = new Stopwatch();
            int frameCountProduced = 0;
            sw.Start();
            while (true)
            {
                Capture capture = null;
                bool isSucceeded;
                try
                {
                    isSucceeded = camera.TryGetCapture(out capture, K4AdotNet.Timeout.FromSeconds(0.1));
                }
                catch
                {
                    isSucceeded = false;
                    capture?.Dispose();
                }

                if (isSucceeded)
                {
                    frameCountProduced++;

                    if (Globals.getInstance().isRecording)
                    {
                        if (threadVideoRecording == null)
                        {
                            threadVideoRecording = new Thread(() => VideoRecordingLoop());
                            threadVideoRecording.Start();
                        }

                        mutVideoRecord.WaitOne();
                        qVideoBufferToRecord.Enqueue(capture);
                        mutVideoRecord.ReleaseMutex();
                    }
                    else
                    {
                        mutVideoDisplay.WaitOne();
                        qVideoBufferToDisplay.Enqueue(capture);
                        mutVideoDisplay.ReleaseMutex();
                    }
                }

                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    fpsProduced = (double)frameCountProduced / sw.Elapsed.TotalSeconds;
                    frameCountProduced = 0;
                    sw.Restart();
                }

                if (isDisposing) {
                    break;
                }
            }
        }

        private void VideoDisplayLoop()
        {
            while (true)
            {
                if (qVideoBufferToDisplay.Count > 0)
                {
                    mutVideoDisplay.WaitOne();
                    while (qVideoBufferToDisplay.Count > 1) {
                        qVideoBufferToDisplay.Dequeue().Dispose();
                    }
                    var capture = qVideoBufferToDisplay.Dequeue();
                    mutVideoDisplay.ReleaseMutex();
                    var image = capture.ColorImage.CreateBitmap();
                    VideoDataAvailable?.Invoke(this, image);
                    capture.Dispose();
                }
                else
                {
                    if (!isDisposing)
                    {
                        Thread.Sleep(30);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            mutVideoDisplay.Dispose();
            while (qVideoBufferToDisplay.Count > 0)
            {
                var capture = qVideoBufferToDisplay.Dequeue();
                capture.Dispose();
            }
        }

        private void AudioDisplay()
        {
            try
            {
                while (true)
                {
                    if (qAudioBufferToDisplay.Count > 0)
                    {
                        var arg = qAudioBufferToDisplay.Dequeue();
                        AudioDataAvailable?.Invoke(audioCaptureDevice, arg);
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (ThreadAbortException abortException)
            {

            }
        }

        

        // cannot overload in this thread, otherwise some data will be lost
        // This is why some other threads are introduced for reducing the workload of this thread
        void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            if (Globals.getInstance().isRecording)
            {
                //if (taskAudioRecording == null) {
                //    taskAudioRecording = Task.Run(()=> AudioRecordingProcessAsync());
                //}
                if (threadAudioRecording == null)
                {
                    threadAudioRecording = new Thread(() => AudioRecordingProcess());
                    threadAudioRecording.Start();
                }

                Byte[] buffer = e.Buffer.ToArray();
                mutAudioFileProcess.WaitOne();
                qAudioBufferToRecord.Enqueue(new WaveInEventArgs(buffer, e.BytesRecorded));
                mutAudioFileProcess.ReleaseMutex();
            }
            Byte[] buf = e.Buffer.ToArray();
            qAudioBufferToDisplay.Enqueue(new WaveInEventArgs(buf, e.BytesRecorded));
        }

        public void StopRecord() {
            //taskAudioRecording = null;
            threadAudioRecording.Join();
            threadAudioRecording = null;

            threadVideoRecording.Join();
            threadVideoRecording = null;
            // flush data into hard disk
            cameraRecorder.Flush();
            cameraRecorder.Dispose();
        }

        //private void DisposeAudioWriters() {
        //    mutAudioFileProcess.WaitOne();

        //    audioFileWriters = null;
        //    mutAudioFileProcess.ReleaseMutex();
        //}

        

        public void Dispose() {
            isDisposing = true;
            camera.StopCameras();
            camera.Dispose();
            mutAudioFileProcess.Dispose();
            mutVideoRecord.Dispose();
            try
            {
                threadVideoDisplay.Join();
                threadVideoDisplay = null;

                threadVideoFrameExtract.Join();
                threadVideoFrameExtract = null;

                threadAudioDisplay.Abort();
                threadAudioDisplay.Join();
                threadAudioDisplay = null;
                AudioDataAvailable = null;
                VideoDataAvailable = null;
                // audio complete
                audioCaptureDevice.DataAvailable -= OnAudioDataAvailable;
                audioCaptureDevice.StopRecording();
                audioCaptureDevice.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"An exception occured when the {field.ToString()}-view Kinect is being closed!");
                Debug.WriteLine(e.ToString());
            }
        }

        ~IntegratedRecorder() {
            //this.Dispose();
        }
    }
}
