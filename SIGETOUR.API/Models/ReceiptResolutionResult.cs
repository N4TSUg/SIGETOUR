using System;
using System.Collections.Generic;
using SIGETOUR.API.Core.Entities;

namespace SIGETOUR.API.Models
{
    public enum ReceiptResolutionStatus
    {
        NoReceipt,
        ExactReceipt,
        Conflict
    }

    public sealed class ReceiptResolutionResult
    {
        public ReceiptResolutionStatus Status { get; }
        public ElectronicReceipt? Receipt { get; }
        public IReadOnlyList<ElectronicReceipt> Candidates { get; }
        public string? ConflictMessage { get; }

        public bool IsNoReceipt => Status == ReceiptResolutionStatus.NoReceipt;
        public bool IsExact => Status == ReceiptResolutionStatus.ExactReceipt;
        public bool IsConflict => Status == ReceiptResolutionStatus.Conflict;

        private ReceiptResolutionResult(
            ReceiptResolutionStatus status,
            ElectronicReceipt? receipt,
            IReadOnlyList<ElectronicReceipt> candidates,
            string? conflictMessage = null)
        {
            Status = status;
            Receipt = receipt;
            Candidates = candidates;
            ConflictMessage = conflictMessage;
        }

        public static ReceiptResolutionResult None() =>
            new ReceiptResolutionResult(ReceiptResolutionStatus.NoReceipt, null, Array.Empty<ElectronicReceipt>());

        public static ReceiptResolutionResult Exact(ElectronicReceipt receipt) =>
            new ReceiptResolutionResult(ReceiptResolutionStatus.ExactReceipt, receipt, new[] { receipt });

        public static ReceiptResolutionResult Conflicted(IReadOnlyList<ElectronicReceipt> candidates, string message) =>
            new ReceiptResolutionResult(ReceiptResolutionStatus.Conflict, null, candidates, message);
    }
}
