﻿using OmniCore.Model.Enums;
using OmniCore.Model.Eros.Data;
using OmniCore.Model.Exceptions;
using OmniCore.Model.Interfaces;
using OmniCore.Model.Interfaces.Data;
using OmniCore.Model.Utilities;
using System;
using System.Diagnostics;

namespace OmniCore.Model.Eros
{
    public class ErosResponse : IMessagePart
    {
        public PartType PartType { get; set;  }

        public Bytes PartData { get; set; }

        public void Parse(IPod pod, IMessageExchangeResult result)
        {
            Debug.WriteLine($"Parsing response type {PartType}");
            switch (PartType)
            {
                case PartType.ResponseVersionInfo:
                    parse_version_response(pod, result);
                    break;
                case PartType.ResponseDetailInfoResponse:
                    parse_information_response(pod, result);
                    break;
                case PartType.ResponseResyncResponse:
                    parse_resync_response(pod as ErosPod, result);
                    break;
                case PartType.ResponseStatus:
                    parse_status_response(pod, result);
                    break;
                default:
                    throw new OmniCoreException(FailureType.PodResponseUnrecognized, $"Unknown response type {PartType}");
            }
        }

        private void parse_version_response(IPod pod, IMessageExchangeResult result)
        {
            bool lengthyResponse = false;
            pod.Created = DateTimeOffset.UtcNow;
            int i = 0;
            if (PartData.Length == 27)
            {
                pod.VersionUnknown = PartData.ToHex(i, i + 7);
                i += 7;
                lengthyResponse = true;
            }

            var mx = PartData.Byte(i++);
            var my = PartData.Byte(i++);
            var mz = PartData.Byte(i++);
            pod.VersionPm = $"{mx}.{my}.{mz}";

            var ix = PartData.Byte(i++);
            var iy = PartData.Byte(i++);
            var iz = PartData.Byte(i++);
            pod.VersionPi = $"{ix}.{iy}.{iz}";

            pod.VersionUnknown += PartData.ToHex(i++, i);

            var status = new ErosStatus() { Created = DateTimeOffset.UtcNow };
            status.Progress = (PodProgress)(PartData.Byte(i++) & 0x0F);

            pod.Lot = PartData.DWord(i);
            pod.Serial = PartData.DWord(i + 4);
            i += 8;
            if (!lengthyResponse)
            {
                var rb = PartData.Byte(i++);
                result.Statistics.PodLowGain = rb >> 6;
                result.Statistics.PodRssi = rb & 0b00111111;
                pod.RadioAddress = PartData.DWord(i);
            }
            else
                pod.RadioAddress = PartData.DWord(i);
            result.Status = status;
        }

        private void parse_information_response(IPod pod, IMessageExchangeResult result)
        {
            int i = 0;
            var rt = PartData.Byte(i++);
            switch (rt)
            {
                case 0x01:
                    var alrs = new ErosAlertStates();

                    alrs.AlertW278 = PartData.Word(i);
                    i += 2;
                    alrs.AlertStates = new uint[]
                    {
                        PartData.Word(i),
                        PartData.Word(i + 2),
                        PartData.Word(i + 4),
                        PartData.Word(i + 6),
                        PartData.Word(i + 8),
                        PartData.Word(i + 10),
                        PartData.Word(i + 12),
                        PartData.Word(i + 14),
                    };
                    result.AlertStates = alrs;
                    break;
                case 0x02:
                    var status = new ErosStatus();
                    var fault = new ErosFault();

                    status.Created = DateTimeOffset.UtcNow;
                    status.Progress = (PodProgress)PartData.Byte(i++);
                    parse_delivery_state(status, PartData.Byte(i++));
                    status.NotDeliveredInsulin = PartData.Byte(i++) * 0.05m;
                    pod.MessageSequence = PartData.Byte(i++);
                    status.DeliveredInsulin = PartData.Byte(i++) * 0.05m;

                    fault.FaultCode = PartData.Byte(i++);
                    fault.FaultRelativeTime = PartData.Word(i);

                    status.Reservoir = PartData.Word(i + 2) * 0.05m;
                    status.ActiveMinutes = PartData.Word(i + 4);
                    i += 6;
                    status.AlertMask = PartData.Byte(i++);
                    fault.TableAccessFault = PartData.Byte(i++);
                    byte f17 = PartData.Byte(i++);
                    fault.InsulinStateTableCorruption = f17 >> 7;
                    fault.InternalFaultVariables = (f17 & 0x60) >> 6;
                    fault.FaultedWhileImmediateBolus = (f17 & 0x10) > 0;
                    fault.ProgressBeforeFault = (PodProgress)(f17 & 0x0F);
                    byte r18 = PartData.Byte(i++);

                    result.Statistics.PodLowGain = (r18 & 0xC0) >> 6;
                    result.Statistics.PodRssi = r18 & 0x3F;

                    fault.ProgressBeforeFault2 = (PodProgress)(PartData.Byte(i++) & 0x0F);
                    fault.FaultInformation2LastWord = PartData.Byte(i++);

                    result.Status = status;
                    result.Fault = fault;
                    break;
                default:
                    throw new OmniCoreException(FailureType.PodResponseUnrecognized, $"Failed to parse the information response of type {rt}");
            }
        }

        private void parse_delivery_state(IStatus podStatus, byte delivery_state)
        {
            if ((delivery_state & 8) > 0)
                podStatus.BolusState = BolusState.Extended;
            else if ((delivery_state & 4) > 0)
                podStatus.BolusState = BolusState.Immediate;
            else
                podStatus.BolusState = BolusState.Inactive;

            if ((delivery_state & 2) > 0)
                podStatus.BasalState = BasalState.Temporary;
            else if ((delivery_state & 1) > 0)
                podStatus.BasalState = BasalState.Scheduled;
            else
                podStatus.BasalState = BasalState.Suspended;
        }

        private void parse_resync_response(ErosPod pod, IMessageExchangeResult result)
        {
            if (PartData[0] == 0x14)
                pod.RuntimeVariables.NonceSync = PartData.Word(1);
            else
                throw new OmniCoreException(FailureType.PodResponseUnrecognized, $"Unknown resync request {PartData} from pod");
        }

        private void parse_status_response(IPod pod, IMessageExchangeResult result)
        {
            var status = new ErosStatus();
            status.Created = DateTimeOffset.UtcNow;
            var s0 = PartData[0];
            uint s1 = PartData.DWord(1);
            uint s2 = PartData.DWord(5);

            parse_delivery_state(status, (byte)(s0 >> 4));
            status.Progress = (PodProgress)(s0 & 0xF);

            pod.MessageSequence = (int)(s1 & 0x00007800) >> 11;
            status.DeliveredInsulin = ((s1 & 0x0FFF8000) >> 15) * 0.05m;
            status.NotDeliveredInsulin = (s1 & 0x000007FF) * 0.05m;
            status.Faulted = ((s2 >> 31) != 0);
            status.AlertMask = (byte)((s2 >> 23) & 0xFF);
            status.ActiveMinutes = (uint)((s2 & 0x007FFC00) >> 10);
            status.Reservoir = (s2 & 0x000003FF) * 0.05m;
            result.Status = status;
        }
    }
}
