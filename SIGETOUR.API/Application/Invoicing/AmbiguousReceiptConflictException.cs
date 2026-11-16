using System;
using System.Collections.Generic;

namespace SIGETOUR.API.Application.Invoicing
{
    public class AmbiguousReceiptConflictException : InvalidOperationException
    {
        public Guid BookingId { get; }
        public IReadOnlyList<Guid> CandidateReceiptIds { get; }

        public AmbiguousReceiptConflictException(
            Guid bookingId,
            IReadOnlyList<Guid> candidateReceiptIds,
            string message)
            : base(message)
        {
            BookingId = bookingId;
            CandidateReceiptIds = candidateReceiptIds;
        }
    }
}
