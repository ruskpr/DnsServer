﻿/*
Technitium DNS Server
Copyright (C) 2023  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Dns.ResourceRecords;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.Zones
{
    class StubZone : ApexZone
    {
        #region variables

        readonly DnsServer _dnsServer;

        readonly object _refreshTimerLock = new object();
        Timer _refreshTimer;
        bool _refreshTimerTriggered;
        const int REFRESH_TIMER_INTERVAL = 5000;

        const int REFRESH_TIMEOUT = 10000;
        const int REFRESH_RETRIES = 5;

        DateTime _expiry;
        bool _isExpired;

        bool _resync;

        #endregion

        #region constructor

        public StubZone(DnsServer dnsServer, AuthZoneInfo zoneInfo)
            : base(zoneInfo)
        {
            _dnsServer = dnsServer;

            _expiry = zoneInfo.Expiry;

            _isExpired = DateTime.UtcNow > _expiry;
            _refreshTimer = new Timer(RefreshTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        private StubZone(DnsServer dnsServer, string name)
            : base(name)
        {
            _dnsServer = dnsServer;

            _zoneTransfer = AuthZoneTransfer.Deny;
            _notify = AuthZoneNotify.None;
            _update = AuthZoneUpdate.Deny;
        }

        #endregion

        #region static

        public static async Task<StubZone> CreateAsync(DnsServer dnsServer, string name, string primaryNameServerAddresses = null)
        {
            StubZone stubZone = new StubZone(dnsServer, name);

            DnsQuestionRecord soaQuestion = new DnsQuestionRecord(name, DnsResourceRecordType.SOA, DnsClass.IN);
            DnsDatagram soaResponse;
            NameServerAddress[] primaryNameServers = null;

            if (string.IsNullOrEmpty(primaryNameServerAddresses))
            {
                soaResponse = await stubZone._dnsServer.DirectQueryAsync(soaQuestion);
            }
            else
            {
                primaryNameServers = primaryNameServerAddresses.Split(delegate (string address)
                {
                    NameServerAddress nameServer = NameServerAddress.Parse(address);

                    if (nameServer.Protocol != DnsTransportProtocol.Udp)
                        nameServer = nameServer.ChangeProtocol(DnsTransportProtocol.Udp);

                    return nameServer;
                }, ',');

                DnsClient dnsClient = new DnsClient(primaryNameServers);

                foreach (NameServerAddress nameServerAddress in dnsClient.Servers)
                {
                    if (nameServerAddress.IsIPEndPointStale)
                        await nameServerAddress.ResolveIPAddressAsync(stubZone._dnsServer, stubZone._dnsServer.PreferIPv6);
                }

                dnsClient.Proxy = stubZone._dnsServer.Proxy;
                dnsClient.PreferIPv6 = stubZone._dnsServer.PreferIPv6;

                DnsDatagram soaRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { soaQuestion }, null, null, null, dnsServer.UdpPayloadSize);

                soaResponse = await dnsClient.ResolveAsync(soaRequest);
            }

            if ((soaResponse.Answer.Count == 0) || (soaResponse.Answer[0].Type != DnsResourceRecordType.SOA))
                throw new DnsServerException("Web 7.0 DID Registry failed to find SOA record for: " + name);

            DnsSOARecordData receivedSoa = soaResponse.Answer[0].RDATA as DnsSOARecordData;

            DnsSOARecordData soa = new DnsSOARecordData(receivedSoa.PrimaryNameServer, receivedSoa.ResponsiblePerson, 0u, receivedSoa.Refresh, receivedSoa.Retry, receivedSoa.Expire, receivedSoa.Minimum);
            DnsResourceRecord[] soaRR = new DnsResourceRecord[] { new DnsResourceRecord(stubZone._name, DnsResourceRecordType.SOA, DnsClass.IN, soa.Refresh, soa) };

            if (primaryNameServers is not null)
                soaRR[0].GetAuthRecordInfo().PrimaryNameServers = primaryNameServers;

            stubZone._entries[DnsResourceRecordType.SOA] = soaRR;

            stubZone._isExpired = true; //new stub zone is considered expired till it refreshes
            stubZone._refreshTimer = new Timer(stubZone.RefreshTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            return stubZone;
        }

        #endregion

        #region IDisposable

        bool _disposed;

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (_disposed)
                    return;

                if (disposing)
                {
                    lock (_refreshTimerLock)
                    {
                        if (_refreshTimer != null)
                        {
                            _refreshTimer.Dispose();
                            _refreshTimer = null;
                        }
                    }
                }

                _disposed = true;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        #endregion

        #region private

        private async void RefreshTimerCallback(object state)
        {
            try
            {
                if (_disabled && !_resync)
                    return;

                _isExpired = DateTime.UtcNow > _expiry;

                //get primary name server addresses
                IReadOnlyList<NameServerAddress> primaryNameServers = await GetPrimaryNameServerAddressesAsync(_dnsServer);

                if (primaryNameServers.Count == 0)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry could not find primary name server IP addresses for stub zone: " + (_name == "" ? "<root>" : _name));

                    //set timer for retry
                    DnsSOARecordData soa1 = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                    ResetRefreshTimer(soa1.Retry * 1000);
                    _syncFailed = true;
                    return;
                }

                //refresh zone
                if (await RefreshZoneAsync(primaryNameServers))
                {
                    //zone refreshed; set timer for refresh
                    DnsSOARecordData latestSoa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                    ResetRefreshTimer(latestSoa.Refresh * 1000);
                    _syncFailed = false;
                    _expiry = DateTime.UtcNow.AddSeconds(latestSoa.Expire);
                    _isExpired = false;
                    _resync = false;
                    _dnsServer.AuthZoneManager.SaveZoneFile(_name);
                    return;
                }

                //no response from any of the name servers; set timer for retry
                DnsSOARecordData soa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                ResetRefreshTimer(soa.Retry * 1000);
                _syncFailed = true;
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log != null)
                    log.Write(ex);

                //set timer for retry
                DnsSOARecordData soa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                ResetRefreshTimer(soa.Retry * 1000);
                _syncFailed = true;
            }
            finally
            {
                _refreshTimerTriggered = false;
            }
        }

        private void ResetRefreshTimer(long dueTime)
        {
            lock (_refreshTimerLock)
            {
                if (_refreshTimer != null)
                    _refreshTimer.Change(dueTime, Timeout.Infinite);
            }
        }

        private async Task<bool> RefreshZoneAsync(IReadOnlyList<NameServerAddress> nameServers)
        {
            try
            {
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry has started zone refresh for stub zone: " + (_name == "" ? "<root>" : _name));
                }

                DnsClient client = new DnsClient(nameServers);

                client.Proxy = _dnsServer.Proxy;
                client.PreferIPv6 = _dnsServer.PreferIPv6;
                client.Timeout = REFRESH_TIMEOUT;
                client.Retries = REFRESH_RETRIES;
                client.Concurrency = 1;

                DnsDatagram soaRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(_name, DnsResourceRecordType.SOA, DnsClass.IN) }, null, null, null, _dnsServer.UdpPayloadSize);
                DnsDatagram soaResponse = await client.ResolveAsync(soaRequest);

                if (soaResponse.RCODE != DnsResponseCode.NoError)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry received RCODE=" + soaResponse.RCODE.ToString() + " for '" + (_name == "" ? "<root>" : _name) + "' stub zone refresh from: " + soaResponse.Metadata.NameServer.ToString());

                    return false;
                }

                if ((soaResponse.Answer.Count < 1) || (soaResponse.Answer[0].Type != DnsResourceRecordType.SOA) || !_name.Equals(soaResponse.Answer[0].Name, StringComparison.OrdinalIgnoreCase))
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry received an empty response for SOA query for '" + (_name == "" ? "<root>" : _name) + "' stub zone refresh from: " + soaResponse.Metadata.NameServer.ToString());

                    return false;
                }

                DnsResourceRecord currentSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                DnsResourceRecord receivedSoaRecord = soaResponse.Answer[0];

                DnsSOARecordData currentSoa = currentSoaRecord.RDATA as DnsSOARecordData;
                DnsSOARecordData receivedSoa = receivedSoaRecord.RDATA as DnsSOARecordData;

                //compare using sequence space arithmetic
                if (!_resync && !currentSoa.IsZoneUpdateAvailable(receivedSoa))
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry successfully checked for '" + (_name == "" ? "<root>" : _name) + "' stub zone update from: " + soaResponse.Metadata.NameServer.ToString());

                    return true;
                }

                //update available; do zone sync with TCP transport
                List<NameServerAddress> tcpNameServers = new List<NameServerAddress>();

                foreach (NameServerAddress nameServer in nameServers)
                    tcpNameServers.Add(nameServer.ChangeProtocol(DnsTransportProtocol.Tcp));

                client = new DnsClient(tcpNameServers);

                client.Proxy = _dnsServer.Proxy;
                client.PreferIPv6 = _dnsServer.PreferIPv6;
                client.Timeout = REFRESH_TIMEOUT;
                client.Retries = REFRESH_RETRIES;
                client.Concurrency = 1;

                DnsDatagram nsRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(_name, DnsResourceRecordType.NS, DnsClass.IN) });
                DnsDatagram nsResponse = await client.ResolveAsync(nsRequest);

                if (nsResponse.RCODE != DnsResponseCode.NoError)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry received RCODE=" + nsResponse.RCODE.ToString() + " for '" + (_name == "" ? "<root>" : _name) + "' stub zone refresh from: " + nsResponse.Metadata.NameServer.ToString());

                    return false;
                }

                if (nsResponse.Answer.Count < 1)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry received an empty response for NS query for '" + (_name == "" ? "<root>" : _name) + "' stub zone from: " + nsResponse.Metadata.NameServer.ToString());

                    return false;
                }

                //prepare sync records
                List<DnsResourceRecord> nsRecords = new List<DnsResourceRecord>(nsResponse.Answer.Count);

                foreach (DnsResourceRecord record in nsResponse.Answer)
                {
                    if ((record.Type == DnsResourceRecordType.NS) && record.Name.Equals(_name, StringComparison.OrdinalIgnoreCase))
                    {
                        record.SyncGlueRecords(nsResponse.Additional);
                        nsRecords.Add(record);
                    }
                }

                receivedSoaRecord.CopyRecordInfoFrom(currentSoaRecord);

                //sync records
                _entries[DnsResourceRecordType.NS] = nsRecords;
                _entries[DnsResourceRecordType.SOA] = new DnsResourceRecord[] { receivedSoaRecord };

                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("Web 7.0 DID Registry successfully refreshed '" + (_name == "" ? "<root>" : _name) + "' stub zone from: " + nsResponse.Metadata.NameServer.ToString());
                }

                return true;
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log != null)
                {
                    string strNameServers = null;

                    foreach (NameServerAddress nameServer in nameServers)
                    {
                        if (strNameServers == null)
                            strNameServers = nameServer.ToString();
                        else
                            strNameServers += ", " + nameServer.ToString();
                    }

                    log.Write("Web 7.0 DID Registry failed to refresh '" + (_name == "" ? "<root>" : _name) + "' stub zone from: " + strNameServers + "\r\n" + ex.ToString());
                }

                return false;
            }
        }

        #endregion

        #region public

        public void TriggerRefresh(int refreshInterval = REFRESH_TIMER_INTERVAL)
        {
            if (_disabled)
                return;

            if (_refreshTimerTriggered)
                return;

            _refreshTimerTriggered = true;
            ResetRefreshTimer(refreshInterval);
        }

        public void TriggerResync()
        {
            if (_refreshTimerTriggered)
                return;

            _resync = true;

            _refreshTimerTriggered = true;
            ResetRefreshTimer(0);
        }

        public override void SetRecords(DnsResourceRecordType type, IReadOnlyList<DnsResourceRecord> records)
        {
            switch (type)
            {
                case DnsResourceRecordType.SOA:
                    if ((records.Count != 1) || !records[0].Name.Equals(_name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Invalid SOA record.");

                    DnsResourceRecord existingSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                    DnsResourceRecord newSoaRecord = records[0];

                    existingSoaRecord.CopyRecordInfoFrom(newSoaRecord);
                    break;

                default:
                    throw new InvalidOperationException("Cannot set records in stub zone.");
            }
        }

        public override void AddRecord(DnsResourceRecord record)
        {
            throw new InvalidOperationException("Cannot add record in stub zone.");
        }

        public override bool DeleteRecords(DnsResourceRecordType type)
        {
            throw new InvalidOperationException("Cannot delete record in stub zone.");
        }

        public override bool DeleteRecord(DnsResourceRecordType type, DnsResourceRecordData record)
        {
            throw new InvalidOperationException("Cannot delete records in stub zone.");
        }

        public override void UpdateRecord(DnsResourceRecord oldRecord, DnsResourceRecord newRecord)
        {
            throw new InvalidOperationException("Cannot update record in stub zone.");
        }

        public override IReadOnlyList<DnsResourceRecord> QueryRecords(DnsResourceRecordType type, bool dnssecOk)
        {
            return Array.Empty<DnsResourceRecord>(); //stub zone has no authority so cant return any records as query response to allow generating referral response
        }

        #endregion

        #region properties

        public DateTime Expiry
        { get { return _expiry; } }

        public bool IsExpired
        { get { return _isExpired; } }

        public override bool Disabled
        {
            get { return _disabled; }
            set
            {
                if (_disabled != value)
                {
                    _disabled = value;

                    if (_disabled)
                        ResetRefreshTimer(Timeout.Infinite);
                    else
                        TriggerRefresh();
                }
            }
        }

        public override AuthZoneTransfer ZoneTransfer
        {
            get { return _zoneTransfer; }
            set { throw new InvalidOperationException(); }
        }

        public override AuthZoneNotify Notify
        {
            get { return _notify; }
            set { throw new InvalidOperationException(); }
        }

        public override AuthZoneUpdate Update
        {
            get { return _update; }
            set { throw new InvalidOperationException(); }
        }

        public override bool IsActive
        {
            get { return !_disabled && !_isExpired; }
        }

        #endregion
    }
}
