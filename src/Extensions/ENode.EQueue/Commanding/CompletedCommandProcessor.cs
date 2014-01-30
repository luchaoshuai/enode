﻿using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using ECommon.IoC;
using ECommon.Scheduling;
using ECommon.Serializing;
using ECommon.Socketing;
using ECommon.Utilities;
using ENode.Commanding;
using EQueue.Clients.Consumers;
using EQueue.Protocols;

namespace ENode.EQueue.Commanding
{
    public class CompletedCommandProcessor : IMessageHandler
    {
        private readonly Consumer _consumer;
        private readonly IBinarySerializer _binarySerializer;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CommandResult>> _processingCommandDict;
        private readonly ConcurrentDictionary<object, TaskCompletionSource<CommandResult>> _processingProcessDict;
        private readonly BlockingCollection<EventStreamData> _queue;
        private readonly Worker _worker;

        public Consumer Consumer { get { return _consumer; } }

        public CompletedCommandProcessor()
            : this(ConsumerSetting.Default)
        {
        }
        public CompletedCommandProcessor(ConsumerSetting setting)
            : this(setting, null)
        {
        }
        public CompletedCommandProcessor(ConsumerSetting setting, string groupName)
            : this(setting, null, groupName)
        {
        }
        public CompletedCommandProcessor(ConsumerSetting setting, string name, string groupName)
            : this(string.Format("{0}@{1}@{2}", SocketUtils.GetLocalIPV4(), string.IsNullOrEmpty(name) ? typeof(CompletedCommandProcessor).Name : name, ObjectId.GenerateNewId()), setting, groupName)
        {
        }
        public CompletedCommandProcessor(string id, ConsumerSetting setting, string groupName)
        {
            _consumer = new Consumer(id, setting, string.IsNullOrEmpty(groupName) ? typeof(CompletedCommandProcessor).Name + "Group" : groupName, this);
            _processingCommandDict = new ConcurrentDictionary<Guid, TaskCompletionSource<CommandResult>>();
            _processingProcessDict = new ConcurrentDictionary<object, TaskCompletionSource<CommandResult>>();
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _queue = new BlockingCollection<EventStreamData>(new ConcurrentQueue<EventStreamData>());
            _worker = new Worker(() =>
            {
                var eventStreamData = _queue.Take();
                var taskCompletionSource = default(TaskCompletionSource<CommandResult>);

                if (eventStreamData.HasProcessCompletedEvent)
                {
                    if (_processingProcessDict.TryGetValue(eventStreamData.AggregateRootId, out taskCompletionSource))
                    {
                        taskCompletionSource.SetResult(CommandResult.Success);
                    }
                }
                else
                {
                    if (_processingCommandDict.TryGetValue(eventStreamData.CommandId, out taskCompletionSource))
                    {
                        taskCompletionSource.SetResult(CommandResult.Success);
                    }
                }
            });
        }

        public CompletedCommandProcessor RegisterProcessingCommand(ICommand command, TaskCompletionSource<CommandResult> taskCompletionSource)
        {
            if (command is IStartProcessCommand)
            {
                _processingProcessDict.TryAdd(((IStartProcessCommand)command).ProcessId, taskCompletionSource);
            }
            else
            {
                _processingCommandDict.TryAdd(command.Id, taskCompletionSource);
            }
            return this;
        }
        public CompletedCommandProcessor NotifyCommandSendFailed(ICommand command)
        {
            var taskCompletionSource = default(TaskCompletionSource<CommandResult>);
            if (_processingCommandDict.TryGetValue(command.Id, out taskCompletionSource))
            {
                taskCompletionSource.SetResult(new CommandResult("Command send failed."));
            }
            return this;
        }
        public CompletedCommandProcessor Start()
        {
            _consumer.Start();
            _worker.Start();
            return this;
        }
        public CompletedCommandProcessor Subscribe(string topic)
        {
            _consumer.Subscribe(topic);
            return this;
        }
        public CompletedCommandProcessor Shutdown()
        {
            _consumer.Shutdown();
            _worker.Stop();
            return this;
        }

        void IMessageHandler.Handle(QueueMessage message, IMessageContext context)
        {
            var eventStreamData = _binarySerializer.Deserialize(message.Body, typeof(EventStreamData)) as EventStreamData;
            _queue.Add(eventStreamData);
        }
    }
}