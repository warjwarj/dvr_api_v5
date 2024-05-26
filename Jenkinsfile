pipeline {
    agent any

    environment {
        IMAGE_NAME = 'dvr_api:latest'
        CONTAINER_NAME = 'dvr_api-container'
    }

    stages {
        stage('Checkout') {
            steps {
                // Checkout the code from the repository configured for this job
                checkout scm
            }
        }

        stage('Build') {
            steps {
                script {
                    // Build the .NET application inside a Docker container
                    docker.image('mcr.microsoft.com/dotnet/sdk:5.0').inside {
                        sh 'dotnet build'
                    }
                }
            }
        }

        stage('Test') {
            steps {
                script {
                    // Test the .NET application inside a Docker container
                    docker.image('mcr.microsoft.com/dotnet/sdk:5.0').inside {
                        sh 'dotnet test'
                    }
                }
            }
        }

        stage('Build Docker Image') {
            steps {
                script {
                    // Build the Docker image for the .NET application
                    sh "docker build -t ${IMAGE_NAME} ."
                }
            }
        }

        stage('Deploy') {
            steps {
                script {
                    // Deploy the Docker container
                     sh "docker stop ${CONTAINER_NAME} || true"
                     sh "docker rm ${CONTAINER_NAME} || true"
                     sh "docker run -d --name ${CONTAINER_NAME} -p 80:80 ${IMAGE_NAME}"
                }
            }
        }
    }

    post {
        always {
            // Clean up Docker containers
            script {
                sh 'docker system prune -f'
            }
        }
        success {
            echo 'Pipeline completed successfully!'
        }
        failure {
            echo 'Pipeline failed!'
        }
    }
}
