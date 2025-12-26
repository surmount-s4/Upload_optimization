This explanatory document summarizes the architectural design and logic for the **200 GB High-Throughput Data Pipeline** we have discussed. It is designed to handle massive file transfers over a LAN using a distributed responsibility model between a local agent, a backend coordinator, and MinIO storage.

---

## 1. Pipeline Overview

The goal is to transfer a 200 GB file from a Client (64 GB RAM) to a Server (128 GB RAM + MinIO) without saturating the server's memory or risking data corruption.

**The "Coordinator-Worker" Pattern:**

* **The Backend (Coordinator):** Manages permissions and "metadata" only.
* **The Agent (Worker):** Handles the "heavy lifting" (reading, hashing, and pushing bytes).
* **The Frontend (Monitor):** Provides the user interface and progress visualization.

---

## 2. Component Modules

### A. The Storage (MinIO Server)

* **Role:** The final destination for the data.
* **Configuration:**
* **Erasure Coding:** Optimized for high-speed parallel writes.
* **CORS Policy:** Configured to allow `PUT` requests from the Agent's origin.
* **Staging Area:** Natively manages partial chunks in a hidden `.minio.sys/multipart` directory until completion.



### B. The Orchestrator (Backend API)

* **Role:** The authority that issues "permission slips" (Presigned URLs).
* **Endpoints:**
1. **`POST /initiate`:** Calls MinIO to start a `MultipartUpload` and returns a unique `UploadId`.
2. **`GET /presign`:** Generates time-limited URLs for specific file chunks (e.g., Part 1, Part 2).
3. **`POST /complete`:** Finalizes the upload by sending the list of ETags to MinIO.



### C. The Client Agent (Windows Service)

* **Role:** A standalone background service that executes the transfer.
* **Key Modules:**
* **Local WebSocket Server:** Communicates real-time progress to the Frontend tab.
* **File Locker:** Opens the 200 GB file with `FileShare.Read` to prevent user deletion/modification during transfer.
* **Parallel Worker Pool:** Multi-threaded engine that reads file "slices" and uploads 4–8 chunks simultaneously.
* **State Manifest:** A local SQLite or JSON file that tracks which chunks are finished to allow for **Resume-on-Crash**.



### D. The Frontend (Web UI)

* **Role:** User dashboard.
* **Logic:** Connects to the Agent via `ws://localhost:[port]` to display speed, ETA, and percentage based on the Agent's internal state.

---

## 3. The End-to-End Data Flow

### Phase 1: Preparation

1. User selects a file in the **Frontend**.
2. The **Frontend** tells the **Agent** the file path.
3. The **Agent** locks the file and calculates an initial "File Fingerprint" (Size + Last Modified).
4. The **Orchestrator** initializes the upload in **MinIO** and gets an `UploadID`.

### Phase 2: The Marathon (The Transfer)

1. The **Agent** requests a batch of **Presigned URLs** from the **Orchestrator**.
2. The **Agent** reads 128 MB of the file into memory.
3. The **Agent** uploads that 128 MB directly to **MinIO** using the Presigned URL.
4. **MinIO** responds with an **ETag** (Receipt). The Agent saves this receipt locally.
5. The **Agent** sends a progress update to the **Frontend** via WebSockets.

### Phase 3: Finalization & Verification

1. Once all ~1600 parts are done, the **Agent** sends the full list of ETags to the **Orchestrator**.
2. The **Orchestrator** calls **MinIO's** `CompleteMultipartUpload`.
3. **MinIO** "stitches" the file (metadata operation).
4. The **Orchestrator** verifies the final object hash against the Agent's original hash.
5. Success is broadcasted: Orchestrator  Agent  Frontend.

---

## 4. Reliability & Safety Reasoning

| Scenario | Solution |
| --- | --- |
| **Network Fluctuation** | Per-chunk retry logic. If 128 MB fails, only that 128 MB is retried. |
| **Power Outage / Crash** | The Agent re-reads its local **State Manifest** on reboot and resumes from the last ETag. |
| **Accidental Deletion** | **Windows File Locking** prevents the user or other apps from touching the file until the transfer is "Verified." |
| **Security Leak** | The Agent and Frontend never see the MinIO Secret Keys. They only see short-lived, single-chunk URLs. |

---

## 5. Summary of Why This is Optimal

1. **Memory Efficient:** The maximum RAM used by the Agent is roughly `(Chunk Size * Threads)`, likely under 2 GB, despite the 200 GB file size.
2. **High Throughput:** By bypassing the Orchestrator for data, you remove a "Middleman" bottleneck and utilize the full LAN bandwidth.
3. **Low Risk:** The separation of the Agent (Process) and Frontend (UI) ensures that a browser crash doesn't kill a 10-hour transfer.

**Would you like me to create a "Configuration Checklist" for the Nginx proxy settings to ensure these large chunks aren't blocked at the server gate?**