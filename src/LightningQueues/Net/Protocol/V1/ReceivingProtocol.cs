using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using LightningQueues.Logging;
using LightningQueues.Storage;

namespace LightningQueues.Net.Protocol.V1
{
    public class ReceivingProtocol : IReceivingProtocol
    {
        readonly IMessageStore _store;
        readonly ILogger _logger;
        private readonly IScheduler _scheduler;

        public ReceivingProtocol(IMessageStore store, ILogger logger) : this(store, logger, Scheduler.Default)
        {
        }

        public ReceivingProtocol(IMessageStore store, ILogger logger, IScheduler scheduler)
        {
            _store = store;
            _logger = logger;
            _scheduler = scheduler;
        }
        

        public IObservable<IncomingMessage> ReceiveStream(IObservable<Stream> streams)
        {
            return receiveStream(streams).Timeout(TimeSpan.FromSeconds(5), _scheduler);
        }

        private IObservable<IncomingMessage> receiveStream(IObservable<Stream> streams)
        {
            return from stream in streams.Do(x => _logger.Debug("Starting to read stream."))
                   from length in LengthChunk(stream)
                   from messages in MessagesChunk(stream, length).Do(x => StoreMessages(stream, x))
                   from message in messages
                   select message; 
        }

        public IObservable<int> LengthChunk(Stream stream)
        {
            return Observable.FromAsync(() => stream.ReadBytesAsync(sizeof(int)))
                .Select(x => BitConverter.ToInt32(x, 0))
                .Catch((Exception ex) => sendSerializationError<int>(stream, ex))
                .Do(x => _logger.Debug($"Read in length value of {x}"))
                .Where(x => x >= 0);
        }

        public IObservable<IncomingMessage[]> MessagesChunk(Stream stream, int length)
        {
            return Observable.FromAsync(() => stream.ReadBytesAsync(length))
                .Select(x => x.ToMessages()).Do(x => _logger.Debug("Successfully read messages"))
                .Catch((Exception ex) => sendSerializationError<IncomingMessage[]>(stream, ex));
        }

        private IObservable<T> sendSerializationError<T>(Stream stream, Exception ex)
        {
            _logger.Error("Error deserializing messages", ex);
            return from _ in Observable.FromAsync(() => SendBuffer(stream, Constants.SerializationFailureBuffer))
                   from empty in Observable.Empty<T>()
                   select empty;
        }

        private async Task SendBuffer(Stream stream, byte[] buffer)
        {
            await stream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }

        public async Task StoreMessages(Stream stream, IncomingMessage[] messages)
        {
            var transaction = await BeginTransaction(stream, messages);
            await ReceiveAcknowledgement(stream, transaction);
            await ActualCommit(stream, transaction);
            _logger.Debug("Finished storing messages");
        }

        public async Task<ITransaction> BeginTransaction(Stream stream, IncomingMessage[] messages)
        {
            try
            {
                var transaction = _store.StoreMessages(messages);
                return transaction;
            }
            catch(QueueDoesNotExistException)
            {
                _logger.Info("Received a message for a queue that doesn't exist");
                await SendBuffer(stream, Constants.QueueDoesNotExistBuffer);
                throw;
            }
            catch(Exception ex)
            {
                _logger.Error("Error persisting messages in receiver", ex);
                await SendBuffer(stream, Constants.ProcessingFailureBuffer);
                throw;
            }
        }

        public async Task ReceiveAcknowledgement(Stream stream, ITransaction transaction)
        {
            try
            {
                await SendBuffer(stream, Constants.ReceivedBuffer);

                var ackBuffer = await stream.ReadBytesAsync(Constants.AcknowledgedBuffer.Length).ConfigureAwait(false);

                if(!ackBuffer.SequenceEqual(Constants.AcknowledgedBuffer))
                {
                    _logger.Debug("Received something that wasn't an acknowledgement");
                    throw new IOException("Unexpected acknowledgement from sender");
                }
            }
            catch(Exception)
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task ActualCommit(Stream stream, ITransaction transaction)
        {
            try
            {
                transaction.Commit();
            }
            catch(Exception)
            {
                await SendBuffer(stream, Constants.RevertBuffer);
                throw;
            }
        }
    }
}