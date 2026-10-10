namespace EventsCommon.Services;

public sealed class EmailDeliveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);
