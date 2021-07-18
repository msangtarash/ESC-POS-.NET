using ESCPOS_NET.Emitters.BaseCommandValues;
using ESCPOS_NET.Utilities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace ESCPOS_NET
{
    public abstract partial class BasePrinter : IDisposable
    {
        private bool disposed = false;

        private volatile bool _isMonitoring;

        private CancellationTokenSource _readCancellationTokenSource;
        private CancellationTokenSource _connectivityCancellationTokenSource;

        private readonly int _maxBytesPerWrite = 15000; // max byte chunks to write at once.

        public PrinterStatusEventArgs Status { get; private set; } = null;

        public event EventHandler StatusChanged;

        protected BinaryWriter Writer { get; set; }

        protected BinaryReader Reader { get; set; }

        // This mutex is used to ensure writes/reads that are done at the application level do not overlap with
        // automated status polling.
        protected static Mutex InstanceReadLockMutex = new Mutex();
        protected static Mutex InstanceWriteLockMutex = new Mutex();

        protected ConcurrentQueue<byte> ReadBuffer { get; set; } = new ConcurrentQueue<byte>();

        protected ConcurrentQueue<byte> WriteBuffer { get; set; } = new ConcurrentQueue<byte>();

        protected int BytesWrittenSinceLastFlush { get; set; } = 0;

        protected virtual bool IsConnected => true;

        public string PrinterName { get; protected set; }

        protected BasePrinter()
        {
            PrinterName = Guid.NewGuid().ToString();
            _connectivityCancellationTokenSource = new CancellationTokenSource();
            Task.Factory.StartNew(MonitorConnectivity, _connectivityCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        protected BasePrinter(string printerName)
        {
            PrinterName = printerName;
            _connectivityCancellationTokenSource = new CancellationTokenSource();
            Task.Factory.StartNew(MonitorConnectivity, _connectivityCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }

        protected virtual void Reconnect()
        {
            // Implemented in the network printer
        }

        public virtual void Read()
        {
            while (_isMonitoring)
            {
                var acquiredMutex = InstanceReadLockMutex.WaitOne(5000);
                if (!acquiredMutex)
                {
                    Logging.Logger?.LogError($"[{PrinterName}] Read was unable to acquire mutex...");
                    continue;
                }

                try
                {
                    if (_readCancellationTokenSource != null && _readCancellationTokenSource.IsCancellationRequested)
                    {
                        _readCancellationTokenSource.Token.ThrowIfCancellationRequested();
                    }

                    // Sometimes the serial port lib will throw an exception and read past the end of the queue if a
                    // status changes while data is being written.  We just ignore these bytes.
                    var b = Reader.ReadByte();
                    ReadBuffer.Enqueue(b);
                    DataAvailable();
                }
                catch (OperationCanceledException ex)
                {
                    try
                    {
                        _readCancellationTokenSource.Dispose();
                        _readCancellationTokenSource = null;
                        _isMonitoring = false;
                    }
                    catch
                    {
                        Logging.Logger?.LogDebug($"[{PrinterName}] Swallowing OperationCanceledException... secondary issue during dispose of cancellation token.");
                    }
                    Logging.Logger?.LogDebug($"[{PrinterName}] Swallowing OperationCanceledException... this is used to turn off status monitoring.");

                }
                catch (IOException ex)
                {
                    // Thrown if the printer times out the socket connection
                    // default is 90 seconds
                    Logging.Logger?.LogDebug($"[{PrinterName}] Swallowing IOException... sometimes happens with network printers. Should get reconnected automatically.");
                }
                catch (Exception ex)
                {
                    // Swallow the exception
                    Logging.Logger?.LogDebug($"[{PrinterName}] Swallowing generic read exception... sometimes happens with serial port printers.");
                }
                InstanceReadLockMutex.ReleaseMutex();
            }
        }

        public virtual void Write(params byte[][] arrays)
        {
            Write(ByteSplicer.Combine(arrays));
        }

        public virtual void Write(byte[] bytes)
        {
            if (!IsConnected)
            {
                Logging.Logger?.LogInformation($"[{PrinterName}] Attempted to write but printer isn't connected. Attempting to reconnect...");
                Reconnect();
            }

            if (!IsConnected)
            {
                Logging.Logger?.LogError($"[{PrinterName}] Unrecoverable connectivity error writing to printer.");
                throw new IOException("Unrecoverable connectivity error writing to printer.");
            }

            int bytePointer = 0;
            int bytesLeft = bytes.Length;
            bool hasFlushed = false;
            while (bytesLeft > 0)
            {
                var acquiredMutex = InstanceWriteLockMutex.WaitOne(5000);
                if (!acquiredMutex)
                {
                    Logging.Logger?.LogError($"[{PrinterName}] Write was unable to acquire mutex...");
                    continue;
                }

                int count = Math.Min(_maxBytesPerWrite, bytesLeft);
                try
                {
                    Writer.Write(bytes, bytePointer, count);
                }
                catch (IOException e)
                {
                    Reconnect();
                    if (!IsConnected)
                    {
                        Logging.Logger?.LogError(e, $"[{PrinterName}] Unrecoverable connectivity error writing to printer.");
                        throw new IOException("Unrecoverable connectivity error writing to printer.");
                    }
                    Writer.Write(bytes, bytePointer, count);
                }
                BytesWrittenSinceLastFlush += count;
                if (BytesWrittenSinceLastFlush >= 200)
                {
                    // Immediately trigger a flush before proceeding so the output buffer will not be delayed.
                    hasFlushed = true;
                    Flush(null, null);
                }

                bytePointer += count;
                bytesLeft -= count;
            }

            if (!hasFlushed)
            {
                Task.Run(async () => { await Task.Delay(50); Flush(null, null); });
            }
            InstanceWriteLockMutex.ReleaseMutex();
        }

        protected virtual void Flush(object sender, ElapsedEventArgs e)
        {
            try
            {

                BytesWrittenSinceLastFlush = 0;
                Writer.Flush();
            }
            catch (Exception ex)
            {
                Logging.Logger?.LogError(ex, $"[{PrinterName}] Flush threw exception.");
            }
        }

        public virtual void StartMonitoring()
        {
            if (!_isMonitoring)
            {
                _isMonitoring = true;
                Logging.Logger?.LogDebug($"[{PrinterName}] Started Monitoring.");
                ReadBuffer = new ConcurrentQueue<byte>();

                _readCancellationTokenSource = new CancellationTokenSource();
                Task.Factory.StartNew(Read, _readCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
        }

        public bool? lastConnectionStatus = false;
        public async virtual void MonitorConnectivity()
        {
            while (_connectivityCancellationTokenSource != null && !_connectivityCancellationTokenSource.IsCancellationRequested)
            {
                var connectedStatus = IsConnected;
                if (connectedStatus != lastConnectionStatus)
                {

                    Logging.Logger?.LogDebug($"[{PrinterName}] MonitorConnectivity detected connection status change. Connected: {connectedStatus}.");
                    lastConnectionStatus = connectedStatus;
                    Status = new PrinterStatusEventArgs()
                    {
                        DeviceIsConnected = connectedStatus,
                    };

                    StatusChanged?.Invoke(this, Status);

                    await Task.Delay(500);
                    continue;
                }
                else if (connectedStatus == true)
                {
                    if (!_isMonitoring) // Don't poll if status back is enabled.
                    {
                        //  grab mutexes and check if the system is still connected.

                        Logging.Logger?.LogDebug($"[{PrinterName}] MonitorConnectivity polling printer for status.");

                        var acquiredWriteMutex = InstanceWriteLockMutex.WaitOne(3000);
                        var acquiredReadMutex = InstanceReadLockMutex.WaitOne(3000);
                        if (!acquiredWriteMutex || !acquiredReadMutex)
                        {
                            Logging.Logger?.LogError($"[{PrinterName}] Unable to acquire mutexes to poll for status.");
                        }
                        else
                        {
                            try
                            {
                                Writer.Write(new byte[] { Cmd.GS, ESCPOS_NET.Emitters.BaseCommandValues.Status.RequestStatus, 0x31 });
                                Reader.ReadByte();
                                Status = new PrinterStatusEventArgs()
                                {
                                    DeviceIsConnected = true,
                                };

                                StatusChanged?.Invoke(this, Status);
                            }
                            catch
                            {
                                connectedStatus = false;
                                Logging.Logger?.LogInformation($"[{PrinterName}] MonitorConnectivity: Detected connection failure.");
                                Status = new PrinterStatusEventArgs()
                                {
                                    DeviceIsConnected = false,
                                };

                                StatusChanged?.Invoke(this, Status);
                            }
                        }

                        if (acquiredWriteMutex)
                        {
                            InstanceWriteLockMutex.ReleaseMutex();
                        }
                        if (acquiredReadMutex)
                        {
                            InstanceReadLockMutex.ReleaseMutex();
                        }

                        // Wait a couple seconds (so we don't do this too often)
                        await Task.Delay(3000);
                    }
                }

                if (connectedStatus == false)
                {
                    try
                    {
                        Logging.Logger?.LogInformation($"[{PrinterName}] MonitorConnectivity: Reconnecting...");
                        Reconnect();
                    }
                    catch (Exception e)
                    {
                        Logging.Logger?.LogError(e, $"[{PrinterName}] MonitorConnectivity: Unable to reconnect. Trying again...");
                        lastConnectionStatus = null;
                    }
                    await Task.Delay(3000);
                }
            }
        }

        public virtual void StopMonitoring()
        {
            if (_isMonitoring)
            {
                Logging.Logger?.LogDebug($"[{PrinterName}] Stopping Monitoring...");
                ReadBuffer = new ConcurrentQueue<byte>();

                if (_readCancellationTokenSource != null)
                {
                    _readCancellationTokenSource?.Cancel();
                }
                Logging.Logger?.LogDebug($"[{PrinterName}] Stopped Monitoring.");
                _isMonitoring = false;
            }
        }

        public virtual void DataAvailable()
        {
            if (ReadBuffer.Count() % 4 == 0)
            {
                var bytes = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!ReadBuffer.TryDequeue(out bytes[i]))
                    {
                        // Ran out of bytes unexpectedly.
                        return;
                    }
                }

                TryUpdatePrinterStatus(bytes);

                // TODO: call other update handlers.
            }
        }

        private void TryUpdatePrinterStatus(byte[] bytes)
        {
            var bytesToString = string.Empty;
            var index = 0;
            foreach (var b in bytes)
            {
                bytesToString += $"index[{index}], value[{b}]\n";
                index++;
            }

            Logging.Logger?.LogDebug($"[{PrinterName}] TryUpdatePrinterStatus: {bytesToString}");

            // Check header bits 0, 1 and 7 are 0, and 4 is 1
            if (bytes[0].IsBitNotSet(0) && bytes[0].IsBitNotSet(1) && bytes[0].IsBitSet(4) && bytes[0].IsBitNotSet(7))
            {
                Status = new PrinterStatusEventArgs()
                {
                    // byte[0] == 20 cash drawer closed
                    // byte[0] == 16 cash drawer open
                    // Note some cash drawers do not close properly.
                    IsCashDrawerOpen = bytes[0].IsBitNotSet(2),
                    IsPrinterOnline = bytes[0].IsBitNotSet(3),
                    IsCoverOpen = bytes[0].IsBitSet(5),
                    IsPaperCurrentlyFeeding = bytes[0].IsBitSet(6),
                    IsWaitingForOnlineRecovery = bytes[1].IsBitSet(0),
                    IsPaperFeedButtonPushed = bytes[1].IsBitSet(1),
                    DidRecoverableNonAutocutterErrorOccur = bytes[1].IsBitSet(2),
                    DidAutocutterErrorOccur = bytes[1].IsBitSet(3),
                    DidUnrecoverableErrorOccur = bytes[1].IsBitSet(5),
                    DidRecoverableErrorOccur = bytes[1].IsBitSet(6),
                    IsPaperLow = bytes[2].IsBitSet(0) && bytes[2].IsBitSet(1),
                    IsPaperOut = bytes[2].IsBitSet(2) && bytes[2].IsBitSet(3),
                };
            }

            if (StatusChanged != null)
            {
                Logging.Logger?.LogDebug($"[{PrinterName}] Invoking Status Changed Event Handler...");
                StatusChanged?.Invoke(this, Status);
            }
        }

        ~BasePrinter()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void OverridableDispose() // This method should only be called by the Dispose method.  // It allows synchronous disposing of derived class dependencies with base class disposes.
        {
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    _readCancellationTokenSource?.Cancel();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue during cancellation token cancellation call.");
                }
                try
                {
                    Reader?.Close();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue closing reader.");
                }
                try
                {
                    Reader?.Dispose();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue disposing reader.");
                }
                try
                {
                    Writer?.Close();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue closing writer.");
                }
                try
                {
                    Writer?.Dispose();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue disposing writer.");
                }
                try
                {
                    OverridableDispose();
                }
                catch (Exception e)
                {
                    Logging.Logger?.LogDebug(e, $"[{PrinterName}] Dispose Issue during overridable dispose.");
                }
            }

            disposed = true;
        }
    }
}