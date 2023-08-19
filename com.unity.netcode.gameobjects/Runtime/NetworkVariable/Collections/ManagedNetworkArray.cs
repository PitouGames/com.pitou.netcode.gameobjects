using System;
using System.Collections.Generic;

namespace Unity.Netcode
{
    /// <summary>
    /// Event based <see cref="NetworkVariableBase"/> container for syncing arrays
    /// </summary>
    /// <typeparam name="T">The type for the array</typeparam>
    public class ManagedNetworkArray<T> : NetworkVariableBase where T : INetworkSerializable, IEquatable<T>
    {
        private readonly T[] m_array;
        private readonly List<NetworkArrayEvent<T>> m_DirtyEvents = new List<NetworkArrayEvent<T>>();

        /// <summary>
        /// Delegate type for array changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnArrayChangedDelegate(NetworkArrayEvent<T> changeEvent);

        /// <summary>
        /// The callback to be invoked when the array gets changed
        /// </summary>
        public event OnArrayChangedDelegate OnArrayChanged;

        /// <inheritdoc/>
        /// <param name="values"></param>
        /// <param name="readPerm"></param>
        /// <param name="writePerm"></param>
        public ManagedNetworkArray(int size,
            IEnumerable<T> values = default,
            NetworkVariableReadPermission readPerm = DefaultReadPerm,
            NetworkVariableWritePermission writePerm = DefaultWritePerm)
            : base(readPerm, writePerm)
        {
            if (size < 0) {
                throw new ArgumentOutOfRangeException("size", size, "size should be greater than 0");
            }
            m_array = new T[size];

            // allow null IEnumerable<T> to mean "no values"
            if (values != null) {
                IEnumerator<T> enumerator = values.GetEnumerator();
                for (int i = 0; i < size && enumerator.MoveNext(); i++) {
                    m_array[i] = enumerator.Current;
                }
            }
        }

        /// <inheritdoc />
        public override void ResetDirty()
        {
            base.ResetDirty();
            if (m_DirtyEvents.Count > 0) {
                m_DirtyEvents.Clear();
            }
        }

        /// <inheritdoc />
        public override bool IsDirty()
        {
            // we call the base class to allow the SetDirty() mechanism to work
            return base.IsDirty() || m_DirtyEvents.Count > 0;
        }

        internal void MarkNetworkObjectDirty()
        {
            if (m_NetworkBehaviour == null) {
                return;
            }

            m_NetworkBehaviour.NetworkManager.BehaviourUpdater.AddForUpdate(m_NetworkBehaviour.NetworkObject);
        }

        /// <inheritdoc />
        public override void WriteDelta(FastBufferWriter writer)
        {

            if (base.IsDirty()) {
                writer.WriteValueSafe((ushort)1);
                writer.WriteValueSafe(NetworkArrayEvent<T>.EventType.Full);
                WriteField(writer);

                return;
            }

            writer.WriteValueSafe((ushort)m_DirtyEvents.Count);
            for (int i = 0; i < m_DirtyEvents.Count; i++) {
                NetworkArrayEvent<T> element = m_DirtyEvents[i];
                writer.WriteValueSafe(element.Type);
                switch (element.Type) {
                    case NetworkArrayEvent<T>.EventType.Value: {
                            writer.WriteValueSafe(element.Index);
                            NetworkVariableSerialization<T>.Write(writer, ref element.Value);
                        }
                        break;
                    case NetworkArrayEvent<T>.EventType.Clear: {
                            //Nothing has to be written
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public override void WriteField(FastBufferWriter writer)
        {
            writer.WriteValueSafe((ushort)m_array.Length);
            for (int i = 0; i < m_array.Length; i++) {
                T element = m_array[i];
                NetworkVariableSerialization<T>.Write(writer, ref element);
                m_array[i] = element;
            }
        }

        /// <inheritdoc />
        public override void ReadField(FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort count);
            for (int i = 0; i < count; i++) {
                T value = default;
                NetworkVariableSerialization<T>.Read(reader, ref value);
                m_array[i] = value;
            }
        }

        /// <inheritdoc />
        public override void ReadDelta(FastBufferReader reader, bool keepDirtyDelta)
        {
            reader.ReadValueSafe(out ushort deltaCount);
            for (int i = 0; i < deltaCount; i++) {
                reader.ReadValueSafe(out NetworkArrayEvent<T>.EventType eventType);
                switch (eventType) {
                    case NetworkArrayEvent<T>.EventType.Value: {
                            reader.ReadValueSafe(out int index);
                            T value = default;
                            NetworkVariableSerialization<T>.Read(reader, ref value);
                            if (index >= m_array.Length) {
                                throw new Exception("Shouldn't be here, index is higher than array length");
                            }

                            T previousValue = m_array[index];
                            m_array[index] = value;

                            OnArrayChanged?.Invoke(new NetworkArrayEvent<T> {
                                Type = eventType,
                                Index = index,
                                Value = value,
                                PreviousValue = previousValue
                            });

                            if (keepDirtyDelta) {
                                m_DirtyEvents.Add(new NetworkArrayEvent<T>() {
                                    Type = eventType,
                                    Index = index,
                                    Value = value,
                                    PreviousValue = previousValue
                                });
                                MarkNetworkObjectDirty();
                            }
                        }
                        break;
                    case NetworkArrayEvent<T>.EventType.Clear: {
                            //Read nothing
                            ClearArray();

                            OnArrayChanged?.Invoke(new NetworkArrayEvent<T> {
                                Type = eventType,
                            });

                            if (keepDirtyDelta) {
                                m_DirtyEvents.Add(new NetworkArrayEvent<T>() {
                                    Type = eventType
                                });
                                MarkNetworkObjectDirty();
                            }
                        }
                        break;
                    case NetworkArrayEvent<T>.EventType.Full: {
                            ReadField(reader);
                            ResetDirty();
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            // check write permissions
            if (m_NetworkBehaviour && !CanClientWrite(m_NetworkBehaviour.NetworkManager.LocalClientId)) {
                throw new InvalidOperationException($"Client is not allowed to write to this ManagedNetworkArray");
            }

            ClearArray();

            var listEvent = new NetworkArrayEvent<T>() {
                Type = NetworkArrayEvent<T>.EventType.Clear
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public int Length => m_array.Length;

        /// <inheritdoc />
        public bool Contains(T item)
        {
            return IndexOf(item) != -1;
        }

        /// <inheritdoc />
        public int IndexOf(T item)
        {
            return Array.IndexOf(m_array, item);
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            return ((IEnumerable<T>)m_array).GetEnumerator();
        }

        /// <inheritdoc />
        public T this[int index]
        {
            get => m_array[index];
            set {
                // check write permissions
                if (m_NetworkBehaviour && !CanClientWrite(m_NetworkBehaviour.NetworkManager.LocalClientId)) {
                    throw new InvalidOperationException("Client is not allowed to write to this ManagedNetworkArray");
                }

                T previousValue = m_array[index];
                m_array[index] = value;

                var listEvent = new NetworkArrayEvent<T>() {
                    Type = NetworkArrayEvent<T>.EventType.Value,
                    Index = index,
                    Value = value,
                    PreviousValue = previousValue
                };

                HandleAddListEvent(listEvent);
            }
        }

        private void HandleAddListEvent(NetworkArrayEvent<T> listEvent)
        {
            m_DirtyEvents.Add(listEvent);
            MarkNetworkObjectDirty();
            OnArrayChanged?.Invoke(listEvent);
        }

        private void ClearArray()
        {
            for (int j = 0; j < m_array.Length; j++) {
                m_array[j] = default;
            }
        }

        /// <summary>
        /// This is actually unused left-over from a previous interface
        /// </summary>
        public int LastModifiedTick =>
                // todo: implement proper network tick for NetworkList
                NetworkTickSystem.NoTick;

    }

    /// <summary>
    /// Struct containing event information about changes to a NetworkArray.
    /// </summary>
    /// <typeparam name="T">The type for the list that the event is about</typeparam>
    public struct NetworkArrayEvent<T>
    {
        /// <summary>
        /// Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum EventType : byte
        {
            /// <summary>
            /// Change value at an index. Old value or new value can be null.
            /// </summary>
            Value,

            /// <summary>
            /// Clear
            /// </summary>
            Clear,

            /// <summary>
            /// Full array refresh
            /// </summary>
            Full
        }

        /// <summary>
        /// Enum representing the operation made to the list.
        /// </summary>
        public EventType Type;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public T Value;

        /// <summary>
        /// The previous value when "Value" has changed, if available.
        /// </summary>
        public T PreviousValue;

        /// <summary>
        /// the index changed, added or removed if available
        /// </summary>
        public int Index;
    }
}
