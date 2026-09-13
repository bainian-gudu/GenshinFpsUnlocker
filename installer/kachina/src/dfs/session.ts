/**
 * DFS2 会话生命周期管理。
 *
 * 将会话创建、挑战重试和清理集中在独立模块，下载编排逻辑只依赖
 * 公开的会话函数，避免所有 DFS 代码堆在同一个文件中。
 */
import { log, warn } from '../api/ipc';
import { clearNetworkInsights } from '../networkInsights';
import { invoke } from '../tauri';
import { Dfs2SessionResponse, InsightItem } from '../types';

// 判断错误是否为应重试的网络错误
const isNetworkError = (error: unknown): boolean => {
  const errorStr = JSON.stringify(error);

  // 检查是否为HTTP状态码错误（4xx/5xx），这些不应该重试
  if (errorStr.includes('Session creation failed:')) {
    return false; // HTTP状态错误，不重试
  }

  // 检查 reqwest/hyper 网络库错误结构（这些错误需要重试）
  if (
    errorStr.includes('Failed to send request:') &&
    (errorStr.includes('reqwest::Error') ||
      errorStr.includes('hyper::Error') ||
      errorStr.includes('hyper_util::client::legacy::Error'))
  ) {
    return true;
  }

  // 检查连接相关的 kind 字段（英文错误码不做本地化）
  if (
    errorStr.includes('kind: ConnectionReset') ||
    errorStr.includes('kind: Timeout') ||
    errorStr.includes('kind: ConnectionRefused') ||
    errorStr.includes('kind: NotFound')
  ) {
    return true;
  }

  // 检查常见的网络错误码
  if (/code: (10054|10060|10061)/.test(errorStr)) {
    return true;
  }

  return false;
};

// DFS2 会话管理
export const createDfs2Session = async (
  apiUrl: string,
  chunks?: string[],
  version?: string,
  extras?: string,
): Promise<string> => {
  // 将 extras 字符串解析为 JSON（如果提供）
  let extrasObject: unknown = undefined;
  if (extras && extras.trim() !== '') {
    try {
      extrasObject = JSON.parse(extras);
    } catch (e) {
      throw new Error(`Invalid extras JSON format: ${e}`);
    }
  }

  // 网络错误主重试循环（最多重试 3 次）
  const retryIntervals = [200, 600, 1000]; // 0.2 秒、0.6 秒、1 秒

  for (let retryAttempt = 0; retryAttempt < 3; retryAttempt++) {
    let challengeResponse: string | undefined = undefined;
    let sessionId: string | undefined = undefined;

    try {
      // 挑战处理循环（每次重试最多尝试 3 次）
      for (
        let challengeAttempts = 0;
        challengeAttempts < 3;
        challengeAttempts++
      ) {
        const sessionResponse: Dfs2SessionResponse =
          await invoke<Dfs2SessionResponse>('create_dfs2_session', {
            apiUrl: apiUrl,
            chunks: chunks || undefined,
            version: version || undefined,
            challengeResponse: challengeResponse,
            sessionId: sessionId,
            extras: extrasObject,
          });

        // 成功创建会话
        if (sessionResponse.sid && !sessionResponse.challenge) {
          // 保存会话，供后续清理
          storeDfs2Session(apiUrl, sessionResponse.sid);
          return sessionResponse.sid;
        }

        // 收到挑战
        if (
          sessionResponse.challenge &&
          sessionResponse.data &&
          sessionResponse.sid
        ) {
          console.log(`DFS2 challenge received: ${sessionResponse.challenge}`);

          try {
            if (sessionResponse.challenge === 'web') {
              // 处理网页挑战，失败后立即退出
              challengeResponse = await handleWebChallenge(
                sessionResponse.data,
              );
            } else {
              // 处理计算挑战（MD5、SHA256）
              challengeResponse = await invoke<string>('solve_dfs2_challenge', {
                challengeType: sessionResponse.challenge,
                data: sessionResponse.data,
              });
            }

            sessionId = sessionResponse.sid;
            console.log('Challenge solved, retrying session creation...');
            continue; // 继续挑战处理循环
          } catch (error) {
            if (sessionResponse.challenge === 'web') {
              // 网页挑战失败后立即退出
              throw new Error(`Web challenge failed: ${error}`);
            } else {
              // 非网页挑战失败后重试
              console.warn(
                `Challenge ${sessionResponse.challenge} failed, retrying...`,
              );
              // 重置挑战数据后重试
              challengeResponse = undefined;
              sessionId = undefined;
              continue; // 继续挑战处理循环 用于 重试
            }
          }
        }

        // 响应格式异常
        throw new Error('Invalid session response format');
      }

      // 到这里说明挑战次数已用尽
      throw new Error('Failed to create session after 3 challenge attempts');
    } catch (error) {
      // 检查是否为网络错误且仍有剩余重试次数
      if (isNetworkError(error) && retryAttempt < 2) {
        console.warn(
          `Network error on attempt ${retryAttempt + 1}, retrying in ${retryIntervals[retryAttempt]}ms...`,
          error,
        );
        // 重试前等待
        await new Promise((resolve) =>
          setTimeout(resolve, retryIntervals[retryAttempt]),
        );
        continue; // 继续主重试循环
      }

      // 网页挑战失败和非网络错误不重试
      if (
        error instanceof Error &&
        error.message.includes('Web challenge failed')
      ) {
        throw error;
      }

      // 非网络错误或重试次数耗尽时抛出错误
      if (retryAttempt === 2) {
        throw new Error(
          `Failed to create DFS2 session after ${retryAttempt + 1} attempts: ${error}`,
        );
      }

      throw error;
    }
  }

  // 理论上不会走到这里，保留兜底处理
  throw new Error('Failed to create session: unexpected exit from retry loop');
};

// 网页挑战处理器
const handleWebChallenge = async (challengeData: string): Promise<string> => {
  // 待办：实现网页挑战处理
  // 这里可能需要：
  // - 打开弹窗
  // - 处理验证码
  // - 用户认证
  // - 重定向流程
  // 挑战处理失败时保留错误上下文
  // 当前直接抛出错误，明确提示该功能尚未实现
  throw new Error(
    'Web challenges not implemented yet. Challenge data: ' + challengeData,
  );
};

// DFS2 会话缓存，目前只保存 runInstall 创建的会话
export const dfs2Sessions = new Map<
  string,
  { sessionId: string; baseUrl: string; resId: string }
>();

export const getDfs2Session = (apiUrl: string) => dfs2Sessions.get(apiUrl);

// runInstall 创建会话后保存会话信息
export const storeDfs2Session = (apiUrl: string, sessionId: string): void => {
  const url = new URL(apiUrl);
  const baseUrl = `${url.protocol}//${url.host}`;
  const resId = url.pathname.split('/').pop() || '';

  dfs2Sessions.set(apiUrl, {
    sessionId,
    baseUrl,
    resId,
  });
};

// 清理指定 DFS2 会话
export const cleanupDfs2Session = async (
  apiUrl: string,
  serversSnapshot?: InsightItem[],
): Promise<void> => {
  const sessionInfo = dfs2Sessions.get(apiUrl);
  if (sessionInfo) {
    try {
      log('Ending DFS2 session:', sessionInfo.sessionId);
      const sessionApiUrl = `${sessionInfo.baseUrl}/session/${sessionInfo.sessionId}/${sessionInfo.resId}`;

      // 使用快照上报数据，防止被并发修改
      // 始终结束 会话；有快照时一并上报 insights
      await invoke('end_dfs2_session', {
        sessionApiUrl: sessionApiUrl,
        insights: serversSnapshot ? { servers: serversSnapshot } : undefined,
      });
      log('DFS2 session ended successfully:', sessionInfo.sessionId);
    } catch (error) {
      warn('Failed to end DFS2 session:', sessionInfo.sessionId, error);
    }
    dfs2Sessions.delete(apiUrl);
  }
};

// 清理全部 DFS2 会话（安装结束时调用）
export const cleanupAllDfs2Sessions = async (
  serversSnapshot?: InsightItem[],
): Promise<void> => {
  const cleanupPromises: Promise<void>[] = [];

  for (const apiUrl of dfs2Sessions.keys()) {
    cleanupPromises.push(cleanupDfs2Session(apiUrl, serversSnapshot));
  }

  // 等待所有 会话 清理完成
  await Promise.allSettled(cleanupPromises);

  // 清空统计数据（所有上报完成后）
  // 只有提供快照时才清空（表示正常完成安装流程）
  if (serversSnapshot) {
    clearNetworkInsights();
  }

  // 清理 会话 缓存
  dfs2Sessions.clear();
};
