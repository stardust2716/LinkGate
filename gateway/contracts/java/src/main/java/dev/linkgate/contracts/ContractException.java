package dev.linkgate.contracts;

/** Deliberately contains no supplied value, raw parser error or input cause. */
public final class ContractException extends IllegalArgumentException {
    private static final long serialVersionUID = 1L;
    public ContractException() { super("INVALID_REQUEST"); }
    public String code() { return "INVALID_REQUEST"; }
}
