// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes.Tests;

public static class ExpectedContent
{
    public const string HelmChartContent =
        """
        apiVersion: "v2"
        name: "aspire"
        version: "0.1.0"
        kubeVersion: ">= 1.18.0-0"
        description: "Aspire Helm Chart"
        type: "application"
        keywords:
          - "aspire"
          - "kubernetes"
        appVersion: "0.1.0"
        deprecated: false

        """;

    public const string HelmValuesContent =
        """
        parameters:
          project1:
            PROJECT1_IMAGE: "project1:latest"

        """;

    public const string ProjectOneDeploymentContent =
        """
        ---
        apiVersion: "apps/v1"
        kind: "Deployment"
        metadata:
          name: "project1-deployment"
        spec:
          template:
            metadata:
              labels:
                app: "aspire"
                component: "project1"
            spec:
              containers:
                - image: "{{ .Values.parameters.project1.PROJECT1_IMAGE }}"
                  name: "project1"
                  envFrom:
                    - configMapRef:
                        name: "project1-config"
                  imagePullPolicy: "IfNotPresent"
          selector:
            matchLabels:
              app: "aspire"
              component: "project1"
          replicas: 1
          revisionHistoryLimit: 3
          strategy:
            rollingUpdate:
              maxSurge: 1
              maxUnavailable: 1
            type: "RollingUpdate"

        """;

    public const string MyAppDeploymentContent =
        """
        ---
        apiVersion: "apps/v1"
        kind: "Deployment"
        metadata:
          name: "myapp-deployment"
        spec:
          template:
            metadata:
              labels:
                app: "aspire"
                component: "myapp"
            spec:
              containers:
                - image: "mcr.microsoft.com/dotnet/aspnet:8.0"
                  name: "myapp"
                  envFrom:
                    - configMapRef:
                        name: "myapp-config"
                    - secretRef:
                        name: "myapp-secret"
                  args:
                    - "--cs"
                    - "Url={{ .Values.parameters.myapp.PARAM0 }}, Secret={{ .Values.parameters.myapp.PARAM1 }}"
                  ports:
                    - name: "http"
                      protocol: "HTTP"
                      containerPort: 80
                      hostPort: 80
                  imagePullPolicy: "IfNotPresent"
          selector:
            matchLabels:
              app: "aspire"
              component: "myapp"
          replicas: 1
          revisionHistoryLimit: 3
          strategy:
            rollingUpdate:
              maxSurge: 1
              maxUnavailable: 1
            type: "RollingUpdate"

        """;
}
