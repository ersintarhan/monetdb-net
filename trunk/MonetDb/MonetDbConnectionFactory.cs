/*
 * The contents of this file are subject to the MonetDB Public License
 * Version 1.1 (the "License"); you may not use this file except in
 * compliance with the License. You may obtain a copy of the License at
 * http://monetdb.cwi.nl/Legal/MonetDBLicense-1.1.html
 * 
 * Software distributed under the License is distributed on an "AS IS"
 * basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
 * License for the specific language governing rights and limitations
 * under the License.
 * 
 * The Original Code is MonetDB .NET Client Library.
 * 
 * The Initial Developer of the Original Code is Tim Gebhardt <tim@gebhardtcomputing.com>.
 * Portions created by Tim Gebhardt are Copyright (C) 2007. All Rights Reserved.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;

namespace MonetDb
{
    /// <summary>
    /// Handles the accounting for the connections to the database.  Handles the connection
    /// pooling of the connections.
    /// </summary>
    internal class MonetDbConnectionFactory
    {
        private static Dictionary<string, ConnectionInformation> _connectionInfo = new Dictionary<string, ConnectionInformation>();

        private static Timer _maintenanceTimer = new Timer(1000);

        private MonetDbConnectionFactory()
        {

        }

        static MonetDbConnectionFactory()
        {
            _maintenanceTimer.Elapsed += delegate(object sender, ElapsedEventArgs e)
            {
                _maintenanceTimer.Stop();
                ICollection<ConnectionInformation> _connections = new List<ConnectionInformation>();
                lock (_connectionInfo)
                {
                    _connections = _connectionInfo.Values;
                }

                foreach (ConnectionInformation ci in _connections)
                {
                    lock (ci)
                    {
                        for (int i = 0; i < ci.Active.Count + ci.Busy.Count - ci.Min && ci.Active.Count > 0; i++)
                        {
                            MapiLib.MapiDestroy(ci.Active.Dequeue());
                        }
                    }
                }
                _maintenanceTimer.Start();
            };
            _maintenanceTimer.Start();

            AppDomain.CurrentDomain.DomainUnload += delegate(object sender, EventArgs e)
            {
                ICollection<ConnectionInformation> _connections = new List<ConnectionInformation>();
                lock (_connectionInfo)
                {
                    _connections = _connectionInfo.Values;
                }

                foreach (ConnectionInformation ci in _connections)
                {
                    lock (ci)
                    {
                        for (int i = 0; i < ci.Active.Count + ci.Busy.Count; i++)
                        {
                            MapiLib.MapiDestroy(ci.Active.Dequeue(60000));
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Returns a connection from the connection pool.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="database"></param>
        /// <param name="useSsl"></param>
        /// <param name="maxConn"></param>
        /// <param name="minConn"></param>
        /// <returns></returns>
        public static MapiConnection GetConnection(string host, int port, string username, string password,
                                                   string database, bool useSsl, int minConn, int maxConn)
        {
            if (minConn < 1)
                throw new ArgumentOutOfRangeException("minConn", minConn + "", "The minimum number of connections cannot be less than 1");
            if (maxConn < 1)
                throw new ArgumentOutOfRangeException("maxConn", maxConn + "", "The mamimum number of connections cannot be less than 1");
            if (minConn > maxConn)
                throw new ArgumentException("The maximum number of connections cannot be greater than the minimum number of connections");

            SetupConnections(host, port, username, password, database, useSsl, minConn, maxConn);

            ConnectionInformation info;
            string key = GetConnectionPoolKey(host, port, username, database);
            lock (_connectionInfo)
            {
                info = _connectionInfo[key];
            }

            MapiConnection retval;
            lock (info)
            {
                retval = info.Active.Dequeue();
                info.Busy.Add(retval);
            }
            return retval;
        }

        public static void CloseConnection(MapiConnection connection)
        {
            string host = MapiLib.MapiGetHost(connection);
            int port = connection.Port;
            string username = MapiLib.MapiGetUser(connection);
            string database = MapiLib.MapiGetDbName(connection);

            string key = GetConnectionPoolKey(host, connection.Port, username, database);

            ConnectionInformation info;
            lock (_connectionInfo)
            {
                info = _connectionInfo[key];
            }

            lock (info)
            {
                info.Busy.Remove(connection);
                info.Active.Enqueue(connection);
            }
        }

        private static void SetupConnections(string host, int port, string username, string password,
                                             string database, bool useSsl, int minConn, int maxConn)
        {
            string key = GetConnectionPoolKey(host, port, username, database);
            ConnectionInformation info;
            lock (_connectionInfo)
            {
                if (!_connectionInfo.TryGetValue(key, out info))
                    info = _connectionInfo[key] = new ConnectionInformation(minConn, maxConn);
            }

            lock (info)
            {
                for (int i = info.Active.Count + info.Busy.Count; i < info.Min || (info.Active.Count == 0 && i < info.Max); i++)
                {
                    MapiConnection connection;
                    if (useSsl)
                        connection = MapiLib.MapiConnectSsl(host, port, username, password, "sql", database);
                    else
                        connection = MapiLib.MapiConnect(host, port, username, password, "sql", database);

                    MapiMsg message = MapiLib.MapiError(connection);
                    if(message.Ptr != IntPtr.Zero)
                    {
                        string error = MapiLib.MapiErrorString(connection);
                        throw new MonetDbException(error);
                    }

                    info.Active.Enqueue(connection);
                }
            }
        }

        private static string GetConnectionPoolKey(string host, int port, string username, string database)
        {
            return string.Format("{0}_{1}_{2}_{3}", host, port, username, database);
        }

        private class ConnectionInformation
        {
            public BlockingQueue<MapiConnection> Active = new BlockingQueue<MapiConnection>();
            public List<MapiConnection> Busy = new List<MapiConnection>();
            public int Min, Max;

            public ConnectionInformation(int min, int max)
            {
                Min = min;
                Max = max;
            }
        }
    }
}
