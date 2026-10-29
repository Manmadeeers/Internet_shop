namespace InternetShop.Api.Exceptions;

public sealed class IncorrectOperationException(string message) : Exception(message) { }

public sealed class IncorrectInputDataException(string message) : Exception(message) { }

public sealed class ObjectMissingException(string message) : Exception(message) { }
