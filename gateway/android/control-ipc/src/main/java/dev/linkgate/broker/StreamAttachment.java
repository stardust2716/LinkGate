package dev.linkgate.broker;

import android.os.BadParcelableException;
import android.os.Parcel;
import android.os.ParcelFileDescriptor;
import android.os.Parcelable;

public final class StreamAttachment implements Parcelable {
    public final int status;
    public final ParcelFileDescriptor descriptor;
    public StreamAttachment(int status,ParcelFileDescriptor descriptor){if(status<0||status>6||(status==0)!=(descriptor!=null))throw new IllegalArgumentException("Invalid descriptor result.");this.status=status;this.descriptor=descriptor;}
    @Override public int describeContents(){return descriptor==null?0:CONTENTS_FILE_DESCRIPTOR;}
    @Override public void writeToParcel(Parcel destination,int flags){destination.writeInt(1);destination.writeInt(status);destination.writeTypedObject(descriptor,flags);}
    public static final Creator<StreamAttachment> CREATOR=new Creator<StreamAttachment>() {
        @Override public StreamAttachment createFromParcel(Parcel source){if(source.readInt()!=1)throw new BadParcelableException("Unknown descriptor reply.");int status=source.readInt();ParcelFileDescriptor descriptor=source.readTypedObject(ParcelFileDescriptor.CREATOR);try{return new StreamAttachment(status,descriptor);}catch(RuntimeException failure){if(descriptor!=null)try{descriptor.close();}catch(java.io.IOException ignored){}throw new BadParcelableException("Invalid descriptor reply.");}}
        @Override public StreamAttachment[] newArray(int size){return new StreamAttachment[size];}
    };
}
