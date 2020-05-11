﻿using System;
using System.Threading.Tasks;
using System.Linq;
using AgileConfig.Server.Apisite.Filters;
using AgileConfig.Server.Apisite.Models;
using AgileConfig.Server.Data.Entity;
using AgileConfig.Server.IService;
using Microsoft.AspNetCore.Mvc;
using Agile.Config.Protocol;
using Microsoft.AspNetCore.Authorization;

namespace AgileConfig.Server.Apisite.Controllers
{
    [Authorize]
    [ModelVaildate]
    public class ConfigController : Controller
    {
        private readonly IConfigService _configService;
        private readonly IModifyLogService _modifyLogService;
        private readonly IRemoteServerNodeActionProxy _remoteServerNodeProxy;
        private readonly IServerNodeService _serverNodeService;
        private readonly ISysLogService _sysLogService;
        public ConfigController(
                                IConfigService configService, 
                                IModifyLogService modifyLogService, 
                                IRemoteServerNodeActionProxy remoteServerNodeProxy,
                                IServerNodeService serverNodeService,
                                ISysLogService sysLogService)
        {
            _configService = configService;
            _modifyLogService = modifyLogService;
            _remoteServerNodeProxy = remoteServerNodeProxy;
            _serverNodeService = serverNodeService;
            _sysLogService = sysLogService;
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody]ConfigVM model)
        {
            if (model == null)
            {
                throw new ArgumentNullException("model");
            }

            var oldConfig = await _configService.GetByAppIdKey(model.AppId, model.Group, model.Key);
            if (oldConfig != null)
            {

                return Json(new
                {
                    success = false,
                    message = "配置已存在，请更改输入的信息。"
                });
            }

            var config = new Config();
            config.Id = Guid.NewGuid().ToString("N");
            config.Key = model.Key;
            config.AppId = model.AppId;
            config.Description = model.Description;
            config.Value = model.Value;
            config.Group = model.Group;
            config.Status = ConfigStatus.Enabled;
            config.CreateTime = DateTime.Now;
            config.UpdateTime = null;
            config.OnlineStatus = OnlineStatus.WaitPublish;

            var result = await _configService.AddAsync(config);

            if (result)
            {
                //add modify log 
                await _modifyLogService.AddAsync(new ModifyLog
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConfigId = config.Id,
                    Key = config.Key,
                    Group = config.Group,
                    Value = config.Value,
                    ModifyTime = config.CreateTime
                });
            }

            return Json(new
            {
                success = result,
                message = !result ? "新建配置失败，请查看错误日志" : ""
            });
        }


        [HttpPost]
        public async Task<IActionResult> Edit([FromBody]ConfigVM model)
        {
            if (model == null)
            {
                throw new ArgumentNullException("model");
            }

            var config = await _configService.GetAsync(model.Id);
            var oldConfig = new Config
            {
                Key = config.Key,
                Group = config.Group,
                Value = config.Value
            };
            if (config == null)
            {
                return Json(new
                {
                    success = false,
                    message = "未找到对应的配置项。"
                });
            }

            if (config.Group != model.Group || config.Key != model.Key)
            {
                var anotherConfig = await _configService.GetByAppIdKey(model.AppId, model.Group, model.Key);
                if (anotherConfig != null)
                {
                    return Json(new
                    {
                        success = false,
                        message = "配置键已存在，请重新输入。"
                    });
                }
            }

            config.AppId = model.AppId;
            config.Description = model.Description;
            config.Key = model.Key;
            config.Value = model.Value;
            config.Group = model.Group;
            config.Status = model.Status;
            config.UpdateTime = DateTime.Now;

            var result = await _configService.UpdateAsync(config);

            if (result && !IsOnlyUpdateDescription(config, oldConfig))
            {
                //add modify log 
                await _modifyLogService.AddAsync(new ModifyLog
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConfigId = config.Id,
                    Key = config.Key,
                    Group = config.Group,
                    Value = config.Value,
                    ModifyTime = config.UpdateTime.Value
                });
                
                //notice clients
                var action = new WebsocketAction
                {
                    Action = ActionConst.Update,
                    Item = new ConfigItem { group = config.Group, key = config.Key, value = config.Value },
                    OldItem = new ConfigItem { group = oldConfig.Group, key = oldConfig.Key, value = oldConfig.Value }
                };
                var nodes = await _serverNodeService.GetAllNodesAsync();
                foreach (var node in nodes)
                {
                    await _remoteServerNodeProxy.AppClientsDoActionAsync(node.Address, config.AppId, action);
                }
            }

            return Json(new
            {
                success = result,
                message = !result ? "修改配置失败，请查看错误日志。" : ""
            });
        }

        private bool IsOnlyUpdateDescription(Config newConfig, Config oldConfig)
        {
            return newConfig.Key == oldConfig.Key && newConfig.Group == oldConfig.Group && newConfig.Value == oldConfig.Value;
        }

        [HttpGet]
        public async Task<IActionResult> All()
        {
            var configs = await _configService.GetAllConfigsAsync();

            return Json(new
            {
                success = true,
                data = configs
            });
        }

        [HttpGet]
        public async Task<IActionResult> Search(string appId, string group, string key)
        {
            var configs = await _configService.Search(appId, group, key);
            configs = configs.Where(c => c.Status == ConfigStatus.Enabled)
                .OrderBy(c => c.AppId).ThenBy(c => c.Group).ThenBy(c => c.Key)
                .ToList();

            return Json(new
            {
                success = true,
                data = configs
            });
        }

        [HttpGet]
        public async Task<IActionResult> Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException("id");
            }

            var config = await _configService.GetAsync(id);

            return Json(new
            {
                success = config != null,
                data = config,
                message = config == null ? "未找到对应的配置项。" : ""
            });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException("id");
            }

            var config = await _configService.GetAsync(id);
            if (config == null)
            {
                return Json(new
                {
                    success = false,
                    message = "未找到对应的配置项。"
                });
            }

            config.Status = ConfigStatus.Deleted;

            var result = await _configService.UpdateAsync(config);

            if (result)
            {
                //notice clients
                var action = new WebsocketAction { Action = ActionConst.Remove, Item = new ConfigItem { group = config.Group, key = config.Key, value = config.Value } };
                var nodes = await _serverNodeService.GetAllNodesAsync();
                foreach (var node in nodes)
                {
                    await _remoteServerNodeProxy.AppClientsDoActionAsync(node.Address, config.AppId, action);
                }
            }

            return Json(new
            {
                success = result,
                message = !result ? "修改配置失败，请查看错误日志" : ""
            });
        }

        [HttpGet]
        public async Task<IActionResult> ModifyLogs(string configId)
        {
            if (string.IsNullOrEmpty(configId))
            {
                throw new ArgumentNullException("configId");
            }

            var logs = await _modifyLogService.Search(configId);

            return Json(new
            {
                success = true,
                data = logs.OrderByDescending(l => l.ModifyTime).ToList()
            }); ;
        }

        [HttpPost]
        public async Task<IActionResult> Offline(string configId)
        {
            if (string.IsNullOrEmpty(configId))
            {
                throw new ArgumentNullException("configId");
            }

            var config = await _configService.GetAsync(configId);
            if (config == null)
            {
                return Json(new
                {
                    success = false,
                    message = "未找到对应的配置项。"
                });
            }
            config.OnlineStatus = OnlineStatus.WaitPublish;
            var result = await _configService.UpdateAsync(config);
            if (result)
            {
                await _sysLogService.AddSysLogSync(new SysLog
                {
                    LogTime = DateTime.Now,
                    LogType = SysLogType.Normal,
                    AppId = config.AppId,
                    LogText = $"下线配置【Key】:{config.Key} 【Group】：{config.Group} 【AppId】：{config.AppId}"
                }) ;
                //notice clients the config item is offline
                var action = new WebsocketAction { Action = ActionConst.Remove, Item = new ConfigItem { group = config.Group, key = config.Key, value = config.Value } };
                var nodes = await _serverNodeService.GetAllNodesAsync();
                foreach (var node in nodes)
                {
                    await _remoteServerNodeProxy.AppClientsDoActionAsync(node.Address, config.AppId, action);
                }
            }

            return Json(new
            {
                success = result,
                message = !result ? "下线配置失败，请查看错误日志" : ""
            });
        }

        [HttpPost]
        public async Task<IActionResult> Publish(string configId)
        {
            if (string.IsNullOrEmpty(configId))
            {
                throw new ArgumentNullException("configId");
            }

            var config = await _configService.GetAsync(configId);
            if (config == null)
            {
                return Json(new
                {
                    success = false,
                    message = "未找到对应的配置项。"
                });
            }
            config.OnlineStatus = OnlineStatus.Online;
            var result = await _configService.UpdateAsync(config);
            if (result)
            {
                await _sysLogService.AddSysLogSync(new SysLog
                {
                    LogTime = DateTime.Now,
                    LogType = SysLogType.Normal,
                    AppId = config.AppId,
                    LogText = $"上线配置【Key】:{config.Key} 【Group】：{config.Group} 【AppId】：{config.AppId}"
                });
                //notice clients config item is published
                var action = new WebsocketAction
                {
                    Action = ActionConst.Add,
                    Item = new ConfigItem { group = config.Group, key = config.Key, value = config.Value }
                };
                var nodes = await _serverNodeService.GetAllNodesAsync();
                foreach (var node in nodes)
                {
                    await _remoteServerNodeProxy.AppClientsDoActionAsync(node.Address, config.AppId, action);
                }
            }
            return Json(new
            {
                success = result,
                message = !result ? "上线配置失败，请查看错误日志" : ""
            });
        }
    }
}
