﻿using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    class EntitySpawner : IServerSerializable
    {
        const int MaxEntitiesPerWrite = 10;

        private enum SpawnableType { Item, Character };

        public UInt16 NetStateID
        {
            get;
            private set;
        }

        interface IEntitySpawnInfo
        {
            Entity Spawn();
        }

        class ItemSpawnInfo : IEntitySpawnInfo
        {
            public readonly ItemPrefab Prefab;

            public readonly Vector2 Position;
            public readonly Inventory Inventory;
            public readonly Submarine Submarine;

            public ItemSpawnInfo(ItemPrefab prefab, Vector2 worldPosition)
            {
                Prefab = prefab;
                Position = worldPosition;
            }

            public ItemSpawnInfo(ItemPrefab prefab, Vector2 position, Submarine sub)
            {
                Prefab = prefab;
                Position = position;
                Submarine = sub;
            }
            
            public ItemSpawnInfo(ItemPrefab prefab, Inventory inventory)
            {
                Prefab = prefab;
                Inventory = inventory;
            }

            public Entity Spawn()
            {                
                Item spawnedItem = null;

                if (Inventory != null)
                {
                    spawnedItem = new Item(Prefab, Vector2.Zero, null);
                    Inventory.TryPutItem(spawnedItem, spawnedItem.AllowedSlots);
                }
                else
                {
                    spawnedItem = new Item(Prefab, Position, Submarine);
                }

                return spawnedItem;
            }
        }

        private readonly Queue<IEntitySpawnInfo> spawnQueue;
        private readonly Queue<Entity> removeQueue;

        class SpawnOrRemove
        {
            public readonly Entity Entity;

            public readonly bool Remove = false;

            public SpawnOrRemove(Entity entity, bool remove)
            {
                Entity = entity;
                Remove = remove;
            }
        }
        
        private List<SpawnOrRemove> spawnHistory = new List<SpawnOrRemove>();
        
        public EntitySpawner()
        {
            spawnQueue = new Queue<IEntitySpawnInfo>();
            removeQueue = new Queue<Entity>();
        }

        public void AddToSpawnQueue(ItemPrefab itemPrefab, Vector2 worldPosition)
        {
            if (GameMain.Client != null) return;
            
            spawnQueue.Enqueue(new ItemSpawnInfo(itemPrefab, worldPosition));
        }

        public void AddToSpawnQueue(ItemPrefab itemPrefab, Vector2 position, Submarine sub)
        {
            if (GameMain.Client != null) return;

            spawnQueue.Enqueue(new ItemSpawnInfo(itemPrefab, position, sub));
        }

        public void AddToSpawnQueue(ItemPrefab itemPrefab, Inventory inventory)
        {
            if (GameMain.Client != null) return;

            spawnQueue.Enqueue(new ItemSpawnInfo(itemPrefab, inventory));
        }

        public void AddToRemoveQueue(Entity entity)
        {
            if (GameMain.Client != null) return;

            removeQueue.Enqueue(entity);
        }

        public void AddToRemoveQueue(Item item)
        {
            if (GameMain.Client != null) return;

            removeQueue.Enqueue(item);
            if (item.ContainedItems == null) return;
            foreach (Item containedItem in item.ContainedItems)
            {
                if (containedItem != null) AddToRemoveQueue(containedItem);
            }
        }


        public void Update()
        {
            if (GameMain.Client != null) return;
            
            while (spawnQueue.Count>0)
            {
                var entitySpawnInfo = spawnQueue.Dequeue();

                var spawnedEntity = entitySpawnInfo.Spawn();
                if (spawnedEntity != null) AddToSpawnedList(spawnedEntity);
            }

            while (removeQueue.Count > 0)
            {
                var entity = removeQueue.Dequeue();
                spawnHistory.Add(new SpawnOrRemove(entity, true));

                entity.Remove();
                NetStateID = (UInt16)spawnHistory.Count;
            }
        }

        public void AddToSpawnedList(Entity entity)
        {
            if (GameMain.Server == null) return;
            if (entity == null) return;

            spawnHistory.Add(new SpawnOrRemove(entity, false));

            NetStateID = (UInt16)spawnHistory.Count;
        }

        public void AddToSpawnedList(IEnumerable<Entity> entities)
        {
            if (GameMain.Server == null) return;
            if (entities == null) return;

            foreach (Entity entity in entities)
            {
                spawnHistory.Add(new SpawnOrRemove(entity, false));
                NetStateID = (UInt16)spawnHistory.Count;
            }
        }

        public void ServerWrite(Lidgren.Network.NetBuffer message, Client client, object[] extraData = null)
        {
            if (GameMain.Server == null) return;

            //skip items that the client already knows about
            List<SpawnOrRemove> entities = spawnHistory.Skip((int)client.lastRecvEntitySpawnID).ToList();

            if (entities.Count > MaxEntitiesPerWrite)
            {
                entities = entities.GetRange(0, MaxEntitiesPerWrite);
            }

            message.Write((UInt16)(spawnHistory.IndexOf(entities[0])+1));
            message.WriteRangedInteger(0, MaxEntitiesPerWrite, entities.Count);

            for (int i = 0; i < entities.Count; i++)
            {
                message.Write(entities[i].Remove);

                if (entities[i].Remove)
                {
                    message.Write(entities[i].Entity.ID);
                }
                else
                {
                    if (entities[i].Entity is Item)
                    {
                        message.Write((byte)SpawnableType.Item);
                        ((Item)entities[i].Entity).WriteSpawnData(message);
                    }
                    else if (entities[i].Entity is Character)
                    {
                        message.Write((byte)SpawnableType.Character);
                        ((Character)entities[i].Entity).WriteSpawnData(message);
                    }
                }
            }
        }

        public void ClientRead(ServerNetObject type, Lidgren.Network.NetBuffer message, float sendingTime)
        {
            if (GameMain.Server != null) return;

            UInt16 ID = message.ReadUInt16();            
            var entityCount = message.ReadRangedInteger(0, MaxEntitiesPerWrite);
            for (int i = 0; i < entityCount; i++)
            {
                bool remove = message.ReadBoolean();

                if (remove)
                {
                    ushort entityId = message.ReadUInt16();

                    var entity = Entity.FindEntityByID(entityId);
                    if (entity != null && NetIdUtils.IdMoreRecent((UInt16)(ID + i), NetStateID))
                    {
                        entity.Remove();
                    }
                }
                else
                {
                    switch (message.ReadByte())
                    {
                        case (byte)SpawnableType.Item:
                            Item.ReadSpawnData(message, NetIdUtils.IdMoreRecent((UInt16)(ID + i), NetStateID));
                            break;
                        case (byte)SpawnableType.Character:
                            Character.ReadSpawnData(message, NetIdUtils.IdMoreRecent((UInt16)(ID + i), NetStateID));
                            break;
                        default:
                            DebugConsole.ThrowError("Received invalid entity spawn message (unknown spawnable type)");
                            break;
                    }
                }
            }

            NetStateID = Math.Max((UInt16)(ID + entityCount - 1), NetStateID);
        }


        public void Clear()
        {
            NetStateID = 0;

            spawnQueue.Clear();
            removeQueue.Clear();
            spawnHistory.Clear();
        }
    }
}