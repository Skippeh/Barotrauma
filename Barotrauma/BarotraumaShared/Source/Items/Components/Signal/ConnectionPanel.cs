﻿using Barotrauma.Networking;
using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class ConnectionPanel : ItemComponent, IServerSerializable, IClientSerializable
    {
        public static Wire HighlightedWire;

        public List<Connection> Connections;

        Character user;

        public ConnectionPanel(Item item, XElement element)
            : base(item, element)
        {
            Connections = new List<Connection>();

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString())
                {
                    case "input":                        
                        Connections.Add(new Connection(subElement, item));
                        break;
                    case "output":
                        Connections.Add(new Connection(subElement, item));
                        break;
                }
            }

            IsActive = true;
        }

        public override void OnMapLoaded()
        {
            foreach (Connection c in Connections)
            {
                c.ConnectLinked();
            }
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (user != null && user.SelectedConstruction != item) user = null;
        }

        public override bool Select(Character picker)
        {
            //attaching wires to items with a body is not allowed
            //(signal items remove their bodies when attached to a wall)
            if (item.body != null)
            {
                return false;
            }

            user = picker;
            IsActive = true;
            return true;
        }

        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null || character!=user) return false;

            var powered = item.GetComponent<Powered>();
            if (powered != null)
            {
                if (powered.Voltage < 0.1f) return false;
            }

            float degreeOfSuccess = DegreeOfSuccess(character);
            if (Rand.Range(0.0f, 50.0f) < degreeOfSuccess) return false;

            character.SetStun(5.0f);

            item.ApplyStatusEffects(ActionType.OnFailure, 1.0f, character);

            return true;
        }

        public override void Load(XElement element)
        {
            base.Load(element);
                        
            List<Connection> loadedConnections = new List<Connection>();

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString())
                {
                    case "input":
                        loadedConnections.Add(new Connection(subElement, item));
                        break;
                    case "output":
                        loadedConnections.Add(new Connection(subElement, item));
                        break;
                }
            }
            
            for (int i = 0; i<loadedConnections.Count && i<Connections.Count; i++)
            {
                loadedConnections[i].wireId.CopyTo(Connections[i].wireId, 0);
            }
        }

        public override XElement Save(XElement parentElement)
        {
            XElement componentElement = base.Save(parentElement);

            foreach (Connection c in Connections)
            {
                c.Save(componentElement);
            }

            return componentElement;
        }

        protected override void RemoveComponentSpecific()
        {
            foreach (Connection c in Connections)
            {
                foreach (Wire wire in c.Wires)
                {
                    if (wire == null) continue;

                    if (wire.OtherConnection(c) == null) //wire not connected to anything else
                    {
                        wire.Item.Drop(null);
                    }
                    else
                    {
                        wire.RemoveConnection(item);
                    }
                }
            }
        }

        public void ClientWrite(NetBuffer msg, object[] extraData = null)
        {
            foreach (Connection connection in Connections)
            {
                Wire[] wires = Array.FindAll(connection.Wires, w => w != null);
                msg.WriteRangedInteger(0, Connection.MaxLinked, wires.Length);
                for (int i = 0; i < wires.Length; i++)
                {
                    msg.Write(wires[i].Item.ID);
                }
            }
        }
        
        public void ServerRead(ClientNetObject type, NetBuffer msg, Client c)
        {
            List<Wire>[] wires = new List<Wire>[Connections.Count];

            //read wire IDs for each connection
            for (int i = 0; i < Connections.Count; i++)
            {
                wires[i] = new List<Wire>();

                int wireCount = msg.ReadRangedInteger(0, Connection.MaxLinked);
                for (int j = 0; j < wireCount; j++)
                {
                    ushort wireId = msg.ReadUInt16();

                    Item wireItem = Entity.FindEntityByID(wireId) as Item;
                    if (wireItem == null) continue;

                    Wire wireComponent = wireItem.GetComponent<Wire>();
                    if (wireComponent != null)
                    {
                        wires[i].Add(wireComponent);
                    }
                }
            }

            item.CreateServerEvent(this);

            //check if the character can access this connectionpanel 
            //and all the wires they're trying to connect
            if (!item.CanClientAccess(c)) return;
            for (int i = 0; i < Connections.Count; i++)
            {
                foreach (Wire wire in wires[i])
                {
                    //wire not found in any of the connections yet (client is trying to connect a new wire)
                    //  -> we need to check if the client has access to it
                    if (!Connections.Any(connection => connection.Wires.Contains(wire)))
                    {
                        if (!wire.Item.CanClientAccess(c)) return;
                    }                    
                }
            }
            
            //go through existing wire links
            for (int i = 0; i < Connections.Count; i++)
            {
                for (int j = 0; j < Connection.MaxLinked; j++)
                {
                    Wire existingWire = Connections[i].Wires[j];
                    if (existingWire == null) continue;
                    
                    //existing wire not in the list of new wires -> disconnect it
                    if (!wires[i].Contains(existingWire))
                    {
                        existingWire.RemoveConnection(item);

                        if (existingWire.Connections[0] == null && existingWire.Connections[1] == null)
                        {
                            GameServer.Log(c.Character.Name + " disconnected a wire from " +
                                Connections[i].Item.Name + " (" + Connections[i].Name + ")", ServerLog.MessageType.ItemInteraction);
                        }
                        else if (existingWire.Connections[0] != null)
                        {
                            GameServer.Log(c.Character.Name + " disconnected a wire from " +
                                Connections[i].Item.Name + " (" + Connections[i].Name + ") to " + existingWire.Connections[0].Item.Name + " (" + existingWire.Connections[0].Name + ")", ServerLog.MessageType.ItemInteraction);
                        }
                        else if (existingWire.Connections[1] != null)
                        {
                            GameServer.Log(c.Character.Name + " disconnected a wire from " +
                                Connections[i].Item.Name + " (" + Connections[i].Name + ") to " + existingWire.Connections[1].Item.Name + " (" + existingWire.Connections[1].Name + ")", ServerLog.MessageType.ItemInteraction);
                        }

                        Connections[i].Wires[j] = null;
                    }
                    
                }
            }

            //go through new wires
            for (int i = 0; i < Connections.Count; i++)
            {
                foreach (Wire newWire in wires[i])
                {
                    //already connected, no need to do anything
                    if (Connections[i].Wires.Contains(newWire)) continue;

                    Connections[i].TryAddLink(newWire);
                    newWire.Connect(Connections[i], true, true);

                    var otherConnection = newWire.OtherConnection(Connections[i]);

                    if (otherConnection == null)
                    {
                        GameServer.Log(c.Character.Name + " connected a wire to " +
                            Connections[i].Item.Name + " (" + Connections[i].Name + ")", 
                            ServerLog.MessageType.ItemInteraction);
                    }
                    else
                    {
                        GameServer.Log(c.Character.Name + " connected a wire from " +
                            Connections[i].Item.Name + " (" + Connections[i].Name + ") to " +
                            (otherConnection == null ? "none" : otherConnection.Item.Name + " (" + (otherConnection.Name) + ")"), 
                            ServerLog.MessageType.ItemInteraction);
                    }
                }
            }
        }

        public void ServerWrite(NetBuffer msg, Client c, object[] extraData = null)
        {
            ClientWrite(msg, extraData);
        }

        public void ClientRead(ServerNetObject type, NetBuffer msg, float sendingTime)
        {
            foreach (Connection connection in Connections)
            {
                connection.ClearConnections();
                int wireCount = msg.ReadRangedInteger(0, Connection.MaxLinked);
                for (int i = 0; i < wireCount; i++)
                {
                    ushort wireId = msg.ReadUInt16();

                    Item wireItem = MapEntity.FindEntityByID(wireId) as Item;
                    if (wireItem == null) continue;

                    Wire wireComponent = wireItem.GetComponent<Wire>();
                    if (wireComponent == null) continue;

                    connection.Wires[i] = wireComponent;
                    wireComponent.Connect(connection, false);
                }
            }
        }        
    }
}
