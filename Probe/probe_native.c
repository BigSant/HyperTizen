#include <errno.h>
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <tee_client_api.h>
#include <unistd.h>

typedef int (*secvideo_capture_screen_fn)(int, int, uint32_t *);

/*
 * The public header is not shipped in the TV SDK.  The 23-word layout below
 * is reconstructed from libvideo-capture.so's setParameters/updateParameters
 * pair.  Words 0/1 are plane capacities, 2/3 are requested/output geometry,
 * 4/5 are the Y/C plane pointers, and word 7 receives the colour format.
 */
__attribute__((visibility("default")))
int hypertizen_probe_secvideo_capture(
    uint8_t *y, uint32_t y_capacity, uint8_t *c, uint32_t c_capacity,
    uint32_t requested_width, uint32_t requested_height,
    uint32_t *result_words, uint32_t result_word_count)
{
    void *library;
    secvideo_capture_screen_fn capture;
    uint32_t parameters[23];
    uint32_t copy_words;
    int result;

    if (!y || !c || !result_words || result_word_count == 0)
        return -EINVAL;

    memset(parameters, 0, sizeof(parameters));
    parameters[0] = y_capacity;
    parameters[1] = c_capacity;
    parameters[2] = requested_width;
    parameters[3] = requested_height;
    parameters[4] = (uint32_t)(uintptr_t)y;
    parameters[5] = (uint32_t)(uintptr_t)c;

    library = dlopen("libvideo-capture.so.0", RTLD_NOW | RTLD_LOCAL);
    if (!library)
        return -ENOENT;
    capture = (secvideo_capture_screen_fn)dlsym(
        library, "secvideo_api_capture_screen");
    if (!capture) {
        dlclose(library);
        return -ENOSYS;
    }

    result = capture(0, 0, parameters);
    copy_words = result_word_count < 23 ? result_word_count : 23;
    memcpy(result_words, parameters, copy_words * sizeof(uint32_t));
    dlclose(library);
    return result;
}

typedef int (*rm_noarg_fn)(void);
typedef int (*rm_open_fn)(int *);
typedef int (*rm_fd_fn)(int);
typedef int (*rm_fd_value_fn)(int, int);
typedef int (*rm_fd_pair_fn)(int, int, int);
typedef int (*rm_event_fn)(int, uint32_t *);

__attribute__((visibility("default")))
int hypertizen_probe_rm_encoder(uint32_t *out, uint32_t out_count)
{
    void *library;
    rm_noarg_fn init;
    rm_open_fn encoder_open;
    rm_fd_fn encoder_close, subscribe, unsubscribe, stream_on, stream_off;
    rm_fd_fn subscribe_stream, unsubscribe_stream, set_stream_on, set_stream_off;
    rm_fd_value_fn bitrate, set_bitrate;
    rm_fd_pair_fn resolution, framerate, set_resolution, set_framerate;
    rm_event_fn deqevent;
    uint32_t event[2] = {0, 0};
    int fd = -1, opened = 0, subscribed = 0, streaming = 0;
    int result = -ENOSYS, i;

#define PUT(index, value) do { if ((index) < out_count) out[(index)] = (uint32_t)(value); } while (0)
    if (!out || out_count == 0) return -EINVAL;
    memset(out, 0, out_count * sizeof(uint32_t));
    library = dlopen("/prd/usr/lib/librm-video-capture-impl.so", RTLD_NOW | RTLD_LOCAL);
    if (!library) return -ENOENT;
#define SYM(type, name) name = (type)dlsym(library, "rm_video_capture_impl_" #name)
    init = NULL;
    SYM(rm_open_fn, encoder_open);
    SYM(rm_fd_fn, encoder_close);
    SYM(rm_fd_value_fn, set_bitrate);
    SYM(rm_fd_pair_fn, set_framerate);
    SYM(rm_fd_pair_fn, set_resolution);
    SYM(rm_fd_fn, subscribe_stream);
    SYM(rm_fd_fn, unsubscribe_stream);
    SYM(rm_fd_fn, set_stream_on);
    SYM(rm_fd_fn, set_stream_off);
    SYM(rm_event_fn, deqevent);
    bitrate = set_bitrate;
    resolution = set_resolution;
    framerate = set_framerate;
    subscribe = subscribe_stream;
    unsubscribe = unsubscribe_stream;
    stream_on = set_stream_on;
    stream_off = set_stream_off;
    if (!encoder_open || !encoder_close || !bitrate || !resolution ||
        !framerate || !subscribe || !unsubscribe || !stream_on || !stream_off ||
        !deqevent) goto done;

    result = 0; PUT(0, result);
    result = encoder_open(&fd); PUT(1, result); PUT(2, fd);
    if (result || fd < 0) goto done;
    opened = 1;
    /* Stage-gated while validating the proprietary driver on real hardware. */
    if (out_count <= 16) goto cleanup;
    result = resolution(fd, 320, 180); PUT(3, result); if (result) goto cleanup;
    result = framerate(fd, 1, 24); PUT(4, result); if (result) goto cleanup;
    result = bitrate(fd, 500000); PUT(5, result); if (result) goto cleanup;
    result = subscribe(fd); PUT(6, result); if (result) goto cleanup;
    subscribed = 1;
    result = stream_on(fd); PUT(7, result); if (result) goto cleanup;
    streaming = 1;
    for (i = 0; i < 50; ++i) {
        result = deqevent(fd, event);
        if (result == 0) break;
        usleep(20000);
    }
    PUT(8, result); PUT(9, event[0]); PUT(10, event[1]); PUT(11, i);

cleanup:
    if (streaming) PUT(12, stream_off(fd));
    if (subscribed) PUT(13, unsubscribe(fd));
    if (opened) PUT(14, encoder_close(fd));
done:
    dlclose(library);
    return result;
#undef SYM
#undef PUT
}

#define CMD_SWU_PASSPHRASE 3
#define CMD_SWU_INIT 0
#define CMD_SWU_UPDATE_AES 1
#define CMD_SWU_FINALIZE_AES 2
#define BUFFER_SIZE 0x10000

static const TEEC_UUID swu_uuid = {
    0x22222221, 0, 0, {0, 0, 0, 0, 0, 0, 0, 1}
};

static const TEEC_UUID tzcapture_uuid = {
    0x58d50001, 0x0006, 0x0006,
    {0xa0, 0x6a, 0x39, 0xb2, 0x56, 0xad, 0x7d, 0xe7}
};

__attribute__((visibility("default")))
uint32_t hypertizen_probe_tzcapture(
    uint8_t *y_output, uint32_t y_capacity,
    uint8_t *c_output, uint32_t c_capacity,
    uint32_t *width, uint32_t *height, uint32_t *chroma_full,
    uint32_t *metadata, uint32_t *origin, int32_t *stage)
{
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    TEEC_SharedMemory y = {0}, c = {0}, params = {0};
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    uint32_t *words;
    uint32_t captured_width, captured_height, full;
    size_t y_size, c_size;

    if (width) *width = 0;
    if (height) *height = 0;
    if (chroma_full) *chroma_full = 0;
    if (metadata) *metadata = 0;
    if (origin) *origin = 0;
    if (stage) *stage = 0;
    if (!y_output || !c_output || !width || !height || !chroma_full)
        return TEEC_ERROR_BAD_PARAMETERS;

    result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; return result; }
    result = TEEC_OpenSession(&context, &session, &tzcapture_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 2; goto out_context; }

    y.size = c.size = params.size = 0x80000;
    y.flags = c.flags = params.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &y);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 3; goto out_session; }
    result = TEEC_AllocateSharedMemory(&context, &c);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 4; goto out_y; }
    result = TEEC_AllocateSharedMemory(&context, &params);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 5; goto out_c; }
    memset(y.buffer, 0, y.size);
    memset(c.buffer, 0, c.size);
    memset(params.buffer, 0, params.size);

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INOUT, TEEC_MEMREF_PARTIAL_INOUT,
        TEEC_MEMREF_PARTIAL_INOUT, TEEC_NONE);
    operation.params[0].memref.parent = &y;
    operation.params[0].memref.size = y.size;
    operation.params[1].memref.parent = &c;
    operation.params[1].memref.size = c.size;
    operation.params[2].memref.parent = &params;
    operation.params[2].memref.size = params.size;
    result = TEEC_InvokeCommand(&session, 0, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 6; goto out_params; }

    words = (uint32_t *)params.buffer;
    captured_width = words[4];
    captured_height = words[5];
    full = words[6];
    if (!captured_width || !captured_height ||
        (uint64_t)captured_width * captured_height > 0x80000) {
        result = TEEC_ERROR_BAD_FORMAT;
        if (stage) *stage = 7;
        goto out_params;
    }
    y_size = (size_t)captured_width * captured_height;
    c_size = full ? y_size : y_size / 2;
    if (y_size > y_capacity || c_size > c_capacity) {
        result = TEEC_ERROR_SHORT_BUFFER;
        if (stage) *stage = 8;
        goto out_params;
    }
    memcpy(y_output, y.buffer, y_size);
    memcpy(c_output, c.buffer, c_size);
    *width = captured_width;
    *height = captured_height;
    *chroma_full = full;
    if (metadata) *metadata = words[7];

out_params:
    TEEC_ReleaseSharedMemory(&params);
out_c:
    TEEC_ReleaseSharedMemory(&c);
out_y:
    TEEC_ReleaseSharedMemory(&y);
out_session:
    TEEC_CloseSession(&session);
out_context:
    TEEC_FinalizeContext(&context);
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_open(uint32_t *origin)
{
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    uint32_t local_origin = 0;
    TEEC_Result result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS)
        return result;

    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin)
        *origin = local_origin;
    if (result == TEEC_SUCCESS)
        TEEC_CloseSession(&session);
    TEEC_FinalizeContext(&context);
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_unwrap(const uint8_t *mode_data, uint32_t mode_size,
                                    uint8_t *output, uint32_t output_capacity,
                                    uint32_t *output_size, uint32_t *origin,
                                    int32_t *stage)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    TEEC_SharedMemory input = {0};
    TEEC_SharedMemory result_buffer = {0};
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    FILE *file = NULL;
    size_t encrypted_size;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!mode_data || !output || !output_size || mode_size == 0)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) {
        if (stage) *stage = -errno;
        return TEEC_ERROR_ACCESS_DENIED;
    }

    result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        if (stage) *stage = 1;
        return result;
    }

    input.size = BUFFER_SIZE;
    input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &input);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        TEEC_FinalizeContext(&context);
        if (stage) *stage = 2;
        return result;
    }

    result_buffer.size = BUFFER_SIZE;
    result_buffer.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &result_buffer);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        TEEC_ReleaseSharedMemory(&input);
        TEEC_FinalizeContext(&context);
        if (stage) *stage = 3;
        return result;
    }

    encrypted_size = fread(input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    if (encrypted_size == 0 || encrypted_size == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT;
        if (stage) *stage = 4;
        goto cleanup_memory;
    }
    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) {
        if (stage) *stage = 5;
        goto cleanup_memory;
    }

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_INOUT,
        TEEC_NONE);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = encrypted_size;
    operation.params[0].memref.offset = 0;
    operation.params[1].memref.parent = &result_buffer;
    operation.params[1].memref.size = BUFFER_SIZE;
    operation.params[1].memref.offset = 0;
    operation.params[2].value.a = mode_data[0];
    operation.params[2].value.b = 0;

    result = TEEC_InvokeCommand(&session, CMD_SWU_PASSPHRASE,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result == TEEC_SUCCESS) {
        uint32_t copy_size = (uint32_t)operation.params[1].memref.size;
        if (copy_size > output_capacity) copy_size = output_capacity;
        memcpy(output, result_buffer.buffer, copy_size);
        *output_size = copy_size;
        if (stage) *stage = 6;
    } else if (stage) {
        *stage = 7;
    }

    TEEC_CloseSession(&session);

cleanup_memory:
    TEEC_ReleaseSharedMemory(&result_buffer);
    TEEC_ReleaseSharedMemory(&input);
    TEEC_FinalizeContext(&context);
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_decrypt(const uint8_t *salt, uint32_t salt_size,
                                     const uint8_t *ciphertext, uint32_t ciphertext_size,
                                     uint8_t *output, uint32_t output_capacity,
                                     uint32_t *output_size, uint32_t *origin,
                                     int32_t *stage, uint32_t derivation,
                                     uint32_t key_size, uint32_t mode)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    TEEC_SharedMemory input = {0};
    TEEC_SharedMemory output_memory = {0};
    TEEC_SharedMemory salt_memory = {0};
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result = TEEC_ERROR_GENERIC;
    FILE *file = NULL;
    size_t key_bytes = 0;
    uint32_t update_bytes = 0;
    uint32_t final_bytes = 0;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!salt || !ciphertext || !output || !output_size ||
        salt_size == 0 || salt_size > BUFFER_SIZE ||
        ciphertext_size == 0 || ciphertext_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) {
        if (stage) *stage = -errno;
        return TEEC_ERROR_ACCESS_DENIED;
    }

    result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; goto cleanup_file; }

    input.size = BUFFER_SIZE;
    input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &input);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 2; goto cleanup_context; }

    output_memory.size = BUFFER_SIZE;
    output_memory.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &output_memory);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 3; goto cleanup_input; }

    salt_memory.size = BUFFER_SIZE;
    salt_memory.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &salt_memory);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 4; goto cleanup_output; }

    key_bytes = fread(input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    file = NULL;
    if (key_bytes == 0 || key_bytes == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT;
        if (stage) *stage = 5;
        goto cleanup_salt;
    }
    memcpy(salt_memory.buffer, salt, salt_size);

    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 6; goto cleanup_salt; }

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_VALUE_INPUT, TEEC_VALUE_INPUT);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = key_bytes;
    operation.params[1].memref.parent = &salt_memory;
    operation.params[1].memref.size = salt_size;
    operation.params[2].value.a = 0; /* decrypt */
    operation.params[2].value.b = 1; /* encrypted passphrase */
    operation.params[3].value.a = derivation;
    operation.params[3].value.b = mode + (key_size << 8);
    result = TEEC_InvokeCommand(&session, CMD_SWU_INIT, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 7; goto cleanup_session; }

    memcpy(input.buffer, ciphertext, ciphertext_size);
    memset(output_memory.buffer, 0, BUFFER_SIZE);
    memset(&operation, 0, sizeof(operation));
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_OUTPUT, TEEC_NONE);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = ciphertext_size;
    operation.params[1].memref.parent = &output_memory;
    operation.params[1].memref.size = ciphertext_size;
    result = TEEC_InvokeCommand(&session, CMD_SWU_UPDATE_AES, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 8; goto cleanup_session; }
    update_bytes = operation.params[2].value.a;
    if (update_bytes > output_capacity) {
        result = TEEC_ERROR_SHORT_BUFFER;
        if (stage) *stage = 9;
        goto cleanup_session;
    }
    memcpy(output, output_memory.buffer, update_bytes);

    memset(output_memory.buffer, 0, BUFFER_SIZE);
    memset(&operation, 0, sizeof(operation));
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_OUTPUT, TEEC_VALUE_OUTPUT, TEEC_NONE, TEEC_NONE);
    operation.params[0].memref.parent = &output_memory;
    operation.params[0].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&session, CMD_SWU_FINALIZE_AES, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 10; goto cleanup_session; }
    final_bytes = operation.params[1].value.a;
    if (update_bytes + final_bytes > output_capacity) {
        result = TEEC_ERROR_SHORT_BUFFER;
        if (stage) *stage = 11;
        goto cleanup_session;
    }
    memcpy(output + update_bytes, output_memory.buffer, final_bytes);
    *output_size = update_bytes + final_bytes;
    if (stage) *stage = 12;

cleanup_session:
    TEEC_CloseSession(&session);
cleanup_salt:
    TEEC_ReleaseSharedMemory(&salt_memory);
cleanup_output:
    TEEC_ReleaseSharedMemory(&output_memory);
cleanup_input:
    TEEC_ReleaseSharedMemory(&input);
cleanup_context:
    TEEC_FinalizeContext(&context);
cleanup_file:
    if (file) fclose(file);
    return result;
}

typedef struct {
    TEEC_Context context;
    TEEC_Session session;
    TEEC_SharedMemory input;
    TEEC_SharedMemory output;
    TEEC_SharedMemory salt;
    int context_ready;
    int session_ready;
    int active;
} swu_stream_state;

static swu_stream_state swu_stream;

static void swu_stream_cleanup(void)
{
    if (swu_stream.session_ready)
        TEEC_CloseSession(&swu_stream.session);
    if (swu_stream.salt.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.salt);
    if (swu_stream.output.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.output);
    if (swu_stream.input.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.input);
    if (swu_stream.context_ready)
        TEEC_FinalizeContext(&swu_stream.context);
    memset(&swu_stream, 0, sizeof(swu_stream));
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_begin(
    const uint8_t *salt, uint32_t salt_size, uint32_t *origin, int32_t *stage,
    uint32_t derivation, uint32_t key_size, uint32_t mode)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Operation operation = {0};
    TEEC_Result result;
    uint32_t local_origin = 0;
    FILE *file = NULL;
    size_t key_bytes;

    swu_stream_cleanup();
    if (stage) *stage = 0;
    if (!salt || salt_size == 0 || salt_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) { if (stage) *stage = -errno; return TEEC_ERROR_ACCESS_DENIED; }

    result = TEEC_InitializeContext(NULL, &swu_stream.context);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; goto fail; }
    swu_stream.context_ready = 1;

    swu_stream.input.size = BUFFER_SIZE;
    swu_stream.input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.input);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 2; goto fail; }
    swu_stream.output.size = BUFFER_SIZE;
    swu_stream.output.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.output);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 3; goto fail; }
    swu_stream.salt.size = BUFFER_SIZE;
    swu_stream.salt.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.salt);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 4; goto fail; }

    key_bytes = fread(swu_stream.input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    file = NULL;
    if (key_bytes == 0 || key_bytes == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT; if (stage) *stage = 5; goto fail;
    }
    memcpy(swu_stream.salt.buffer, salt, salt_size);

    result = TEEC_OpenSession(&swu_stream.context, &swu_stream.session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 6; goto fail; }
    swu_stream.session_ready = 1;

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_VALUE_INPUT, TEEC_VALUE_INPUT);
    operation.params[0].memref.parent = &swu_stream.input;
    operation.params[0].memref.size = key_bytes;
    operation.params[1].memref.parent = &swu_stream.salt;
    operation.params[1].memref.size = salt_size;
    operation.params[2].value.a = 0;
    operation.params[2].value.b = 1;
    operation.params[3].value.a = derivation;
    operation.params[3].value.b = mode + (key_size << 8);
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_INIT,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 7; goto fail; }
    swu_stream.active = 1;
    if (stage) *stage = 8;
    return TEEC_SUCCESS;

fail:
    if (file) fclose(file);
    swu_stream_cleanup();
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_update(
    const uint8_t *ciphertext, uint32_t ciphertext_size,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_size,
    uint32_t *origin, int32_t *stage)
{
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    uint32_t produced;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!swu_stream.active || !ciphertext || !output || !output_size ||
        ciphertext_size == 0 || ciphertext_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;
    memcpy(swu_stream.input.buffer, ciphertext, ciphertext_size);
    memset(swu_stream.output.buffer, 0, BUFFER_SIZE);
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_OUTPUT, TEEC_NONE);
    operation.params[0].memref.parent = &swu_stream.input;
    operation.params[0].memref.size = ciphertext_size;
    operation.params[1].memref.parent = &swu_stream.output;
    operation.params[1].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_UPDATE_AES,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; return result; }
    produced = operation.params[2].value.a;
    if (produced > output_capacity) { if (stage) *stage = 2; return TEEC_ERROR_SHORT_BUFFER; }
    memcpy(output, swu_stream.output.buffer, produced);
    *output_size = produced;
    if (stage) *stage = 3;
    return TEEC_SUCCESS;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_finish(
    uint8_t *output, uint32_t output_capacity, uint32_t *output_size,
    uint32_t *origin, int32_t *stage)
{
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    uint32_t produced = 0;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!swu_stream.active || !output || !output_size)
        return TEEC_ERROR_BAD_PARAMETERS;
    memset(swu_stream.output.buffer, 0, BUFFER_SIZE);
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_OUTPUT, TEEC_VALUE_OUTPUT, TEEC_NONE, TEEC_NONE);
    operation.params[0].memref.parent = &swu_stream.output;
    operation.params[0].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_FINALIZE_AES,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result == TEEC_SUCCESS) {
        produced = operation.params[1].value.a;
        if (produced > output_capacity) {
            result = TEEC_ERROR_SHORT_BUFFER;
            if (stage) *stage = 1;
        } else {
            memcpy(output, swu_stream.output.buffer, produced);
            *output_size = produced;
            if (stage) *stage = 2;
        }
    } else if (stage) {
        *stage = 3;
    }
    swu_stream_cleanup();
    return result;
}

__attribute__((visibility("default")))
void hypertizen_probe_swu_stream_abort(void)
{
    swu_stream_cleanup();
}
