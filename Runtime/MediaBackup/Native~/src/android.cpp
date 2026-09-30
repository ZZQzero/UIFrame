#include "ufbackup.h"
#include <jni.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <cstring>
namespace {
ufb_status status(){ufb_status s{};s.size=sizeof(s);s.abi=UFB_ABI;return s;}
// JNI modified UTF-8 is not filesystem UTF-8. Use Java's standard UTF-8 encoder.
std::string utf8(JNIEnv *env,jstring value) {
    if(!value) throw std::invalid_argument("Missing UTF-8 string");
    jclass type=env->GetObjectClass(value);
    jmethodID method=env->GetMethodID(type,"getBytes","(Ljava/lang/String;)[B");
    jstring encoding=env->NewStringUTF("UTF-8");
    auto bytes=static_cast<jbyteArray>(env->CallObjectMethod(value,method,encoding));
    env->DeleteLocalRef(encoding);env->DeleteLocalRef(type);
    if(env->ExceptionCheck() || !bytes) throw std::runtime_error("UTF-8 encoding failed");
    std::string result(size_t(env->GetArrayLength(bytes)),'\0');
    env->GetByteArrayRegion(bytes,0,jsize(result.size()),reinterpret_cast<jbyte*>(result.data()));
    env->DeleteLocalRef(bytes);return result;
}
jstring java_string(JNIEnv *env,const char *value) {
    auto bytes=env->NewByteArray(jsize(std::strlen(value)));if(!bytes)return nullptr;
    env->SetByteArrayRegion(bytes,0,env->GetArrayLength(bytes),reinterpret_cast<const jbyte*>(value));
    auto type=env->FindClass("java/lang/String");
    auto encoding=env->NewStringUTF("UTF-8");
    jstring result=nullptr;
    if(type && encoding && !env->ExceptionCheck()) {
        auto constructor=env->GetMethodID(type,"<init>","([BLjava/lang/String;)V");
        if(constructor)result=static_cast<jstring>(env->NewObject(type,constructor,bytes,encoding));
    }
    env->DeleteLocalRef(encoding);env->DeleteLocalRef(type);env->DeleteLocalRef(bytes);return result;
}
void failure(JNIEnv *env,const ufb_status &s) {
    if(env->ExceptionCheck()) return;
    auto type=env->FindClass("com/zzq/uiframe/media/BackupRepository$Failure");
    if(!type) return;
    auto constructor=env->GetMethodID(type,"<init>","(IIIILjava/lang/String;)V");
    if(!constructor) {env->DeleteLocalRef(type);return;}
    auto message=java_string(env,s.message);if(!message){env->DeleteLocalRef(type);return;}
    auto exception=static_cast<jthrowable>(env->NewObject(type,constructor,s.error,s.sqlite_code,jint(s.phase),s.committed,message));
    if(exception) env->Throw(exception);
    env->DeleteLocalRef(exception);env->DeleteLocalRef(message);env->DeleteLocalRef(type);
}
template<class F> auto boundary(JNIEnv *env,F action)->decltype(action()) {
    try {return action();}
    catch(const std::exception &e) {
        if(!env->ExceptionCheck()) {
            auto type=env->FindClass("java/lang/IllegalStateException");auto message=java_string(env,e.what());
            if(type && message && !env->ExceptionCheck()) {
                auto constructor=env->GetMethodID(type,"<init>","(Ljava/lang/String;)V");
                if(constructor){auto exception=static_cast<jthrowable>(env->NewObject(type,constructor,message));if(exception)env->Throw(exception);env->DeleteLocalRef(exception);}
            }
            env->DeleteLocalRef(message);env->DeleteLocalRef(type);
        }
        return {};
    }
}
}
extern "C" JNIEXPORT jlong JNICALL Java_com_zzq_uiframe_media_BackupRepository_nativeOpen(JNIEnv *env,jclass,jstring root,jstring identity) {
    return boundary(env,[&]()->jlong {auto r=utf8(env,root),id=utf8(env,identity);auto s=status();uint64_t handle=0;
        if(ufbackup_open(r.c_str(),id.c_str(),"","",0,&handle,&s)) failure(env,s);return jlong(handle);});
}
extern "C" JNIEXPORT jint JNICALL Java_com_zzq_uiframe_media_BackupRepository_nativeClose(JNIEnv *env,jclass,jlong handle) {
    auto s=status();auto code=ufbackup_close(uint64_t(handle),&s);if(code) failure(env,s);return code;
}
extern "C" JNIEXPORT jbyteArray JNICALL Java_com_zzq_uiframe_media_BackupRepository_nativeCall(JNIEnv *env,jclass,jlong handle,jint command,jbyteArray input) {
    return boundary(env,[&]()->jbyteArray {
        if(!input) throw std::invalid_argument("Missing backup command");
        auto length=env->GetArrayLength(input);if(length<4 || length>1024*1024) throw std::invalid_argument("Backup command exceeds input budget");
        std::vector<uint8_t> request(static_cast<size_t>(length)),output(1024*1024);
        env->GetByteArrayRegion(input,0,length,reinterpret_cast<jbyte*>(request.data()));if(env->ExceptionCheck()) return nullptr;
        auto s=status();if(ufbackup_call(uint64_t(handle),uint32_t(command),request.data(),uint32_t(length),output.data(),uint32_t(output.size()),&s)) {failure(env,s);return nullptr;}
        auto result=env->NewByteArray(jsize(s.length));if(result) env->SetByteArrayRegion(result,0,jsize(s.length),reinterpret_cast<jbyte*>(output.data()));return result;
    });
}
