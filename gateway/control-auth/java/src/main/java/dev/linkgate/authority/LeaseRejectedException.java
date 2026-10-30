package dev.linkgate.authority;
public final class LeaseRejectedException extends java.security.GeneralSecurityException {
    public final String code="AUTH_REQUIRED";
    public enum Stage {UNSPECIFIED,INPUT,CERTIFICATE,COMPACT,HEADER,CLAIMS,AUTHORITY,SCOPE,TIME,SIGNATURE,STANDARD_CLAIMS,RECEIPT}
    public final Stage stage;
    LeaseRejectedException(){this(Stage.UNSPECIFIED);}
    LeaseRejectedException(Stage stage){super("Server authorization lease rejected.");this.stage=stage;}
}
