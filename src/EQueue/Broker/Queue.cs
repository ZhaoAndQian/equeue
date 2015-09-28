﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ECommon.Components;
using ECommon.Serializing;
using EQueue.Broker.Storage;

namespace EQueue.Broker
{
    public class Queue
    {
        private const string QueueSettingFileName = "queue.setting";
        private readonly TFChunkWriter _chunkWriter;
        private readonly TFChunkReader _chunkReader;
        private readonly TFChunkManager _chunkManager;
        private long _nextOffset = 0;
        private readonly IJsonSerializer _jsonSerializer;
        private QueueSetting _setting;
        private readonly string _queueSettingFile;

        public string Topic { get; private set; }
        public int QueueId { get; private set; }
        public long NextOffset { get { return _nextOffset; } }
        public QueueSetting Setting { get { return _setting; } }

        public Queue(string topic, int queueId)
        {
            Topic = topic;
            QueueId = queueId;

            _jsonSerializer = ObjectContainer.Resolve<IJsonSerializer>();
            _chunkManager = new TFChunkManager(string.Format("{0}-{1}", Topic, QueueId), BrokerController.Instance.Setting.QueueChunkConfig, Topic + @"\" + QueueId);
            _chunkWriter = new TFChunkWriter(_chunkManager);
            _chunkReader = new TFChunkReader(_chunkManager, _chunkWriter);
            _queueSettingFile = Path.Combine(_chunkManager.ChunkPath, QueueSettingFileName);
        }

        public void Load()
        {
            _setting = LoadQueueSetting();
            if (_setting == null)
            {
                _setting = new QueueSetting { Status = QueueStatus.Normal };
                SaveQueueSetting();
            }
            _chunkManager.Load(ReadMessageIndex);
            _chunkWriter.Open();

            var lastChunk = _chunkManager.GetLastChunk();
            var lastOffsetGlobalPosition = lastChunk.DataPosition + lastChunk.ChunkHeader.ChunkDataStartPosition;
            if (lastOffsetGlobalPosition > 0)
            {
                _nextOffset = lastOffsetGlobalPosition / _chunkManager.Config.ChunkDataUnitSize;
            }
        }
        public void Close()
        {
            _chunkWriter.Close();
            _chunkManager.Close();
        }
        public void AddMessage(long messagePosition)
        {
            _chunkWriter.Write(new QueueLogRecord(messagePosition));
        }
        public long GetLastFlushedOffset()
        {
            return (_chunkWriter.CurrentChunk.GlobalFlushPosition / _chunkManager.Config.ChunkDataUnitSize) - 1;
        }
        public long GetMessagePosition(long queueOffset)
        {
            var position = queueOffset * _chunkManager.Config.ChunkDataUnitSize;
            var record = _chunkReader.TryReadAt(position, ReadMessageIndex);
            return record.MessageLogPosition;
        }
        public long GetNextChunkFirstOffset(long queueOffset)
        {
            var position = queueOffset * _chunkManager.Config.ChunkDataUnitSize;
            var chunkNum = (int)(position / _chunkManager.Config.GetChunkDataSize());
            var nextChunkNum = chunkNum + 1;
            var chunk = _chunkManager.GetChunk(nextChunkNum);

            while (chunk == null)
            {
                if (nextChunkNum >= _chunkManager.GetLastChunk().ChunkHeader.ChunkNumber)
                {
                    break;
                }
                nextChunkNum++;
                chunk = _chunkManager.GetChunk(nextChunkNum);
            }

            if (chunk != null)
            {
                return chunk.ChunkHeader.ChunkDataStartPosition / _chunkManager.Config.ChunkDataUnitSize;
            }

            return -1L;
        }
        public void Enable()
        {
            _setting.Status = QueueStatus.Normal;
            SaveQueueSetting();
        }
        public void Disable()
        {
            _setting.Status = QueueStatus.Disabled;
            SaveQueueSetting();
        }
        public long IncrementNextOffset()
        {
            return _nextOffset++;
        }
        public long GetMinQueueOffset()
        {
            return _chunkManager.GetFirstChunk().ChunkHeader.ChunkDataStartPosition / _chunkManager.Config.ChunkDataUnitSize;
        }
        public void DeleteMessages(long minConsumedMessagePosition)
        {
            var chunks = _chunkManager.GetAllChunks().Where(x => x.IsCompleted);

            foreach (var chunk in chunks)
            {
                var maxPosition = chunk.ChunkHeader.ChunkDataEndPosition;
                var record = _chunkReader.TryReadAt(maxPosition, ReadMessageIndex);
                var chunkLastMessagePosition = record.MessageLogPosition;

                if (chunkLastMessagePosition <= minConsumedMessagePosition)
                {
                    _chunkManager.RemoveChunk(chunk);
                }
            }
        }

        private QueueLogRecord ReadMessageIndex(int length, BinaryReader reader)
        {
            var record = new QueueLogRecord();
            record.ReadFrom(length, reader);
            if (record.MessageLogPosition <= 0)
            {
                return null;
            }
            return record;
        }
        private QueueSetting LoadQueueSetting()
        {
            if (!Directory.Exists(_chunkManager.ChunkPath))
            {
                Directory.CreateDirectory(_chunkManager.ChunkPath);
            }
            using (var stream = new FileStream(_queueSettingFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using (var reader = new StreamReader(stream))
                {
                    var text = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return _jsonSerializer.Deserialize<QueueSetting>(text);
                    }
                    return null;
                }
            }
        }
        private void SaveQueueSetting()
        {
            if (!Directory.Exists(_chunkManager.ChunkPath))
            {
                Directory.CreateDirectory(_chunkManager.ChunkPath);
            }
            using (var stream = new FileStream(_queueSettingFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(_jsonSerializer.Serialize(_setting));
                }
            }
        }
    }
    public class QueueSetting
    {
        public QueueStatus Status;
    }
}
